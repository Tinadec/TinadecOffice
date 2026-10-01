using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;

namespace TinadecCore.Runtime;

/// <summary>
/// Bridges a TinaChat executor wake into the existing run context protocol. The message has already
/// passed TinaChat audience checks; this class only applies a CAS context supplement and wakes the
/// durable run. It never invokes a second worker or creates a hidden execution loop.
/// </summary>
internal sealed class ExecutorMessageWakeSink(
    ILifecycleManager lifecycle,
    IConversationStore conversations,
    IFullDuplexRunEngine engine) : IExecutorMessageWakeSink
{
    public async Task<bool> DeliverAsync(
        Guid sessionId,
        Guid runId,
        Guid agentInstanceId,
        string briefing,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            RunState state;
            try
            {
                state = await lifecycle.GetRunStateAsync(runId.ToString("N"), cancellationToken).ConfigureAwait(false);
            }
            catch (KeyNotFoundException)
            {
                // The execution member is offline or its old run was already
                // purged. The TinaChat inbox remains the durable todo; there is
                // no context boundary to wake, so settle the wake instead of
                // retrying it forever.
                return true;
            }
            if (string.IsNullOrWhiteSpace(state.SessionId)
                || state.Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled)
                return true;

            var existing = await conversations.ListRecentContextPatchesAsync(sessionId, runId, 32, cancellationToken).ConfigureAwait(false);
            if (existing.Any(patch => patch.AgentInstanceId == agentInstanceId && string.Equals(patch.Content, briefing, StringComparison.Ordinal)))
            {
                await engine.EnqueueAsync(runId, cancellationToken).ConfigureAwait(false);
                return true;
            }

            var baseRevision = await conversations.GetContextRevisionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            var applied = await conversations.ApplyContextPatchAsync(new ContextPatchRequest(
                sessionId,
                baseRevision,
                briefing,
                "TinaChat message for the execution agent",
                runId,
                agentInstanceId,
                Kind: "supplement"), cancellationToken).ConfigureAwait(false);
            if (string.Equals(applied.Status, "applied", StringComparison.OrdinalIgnoreCase))
            {
                await engine.EnqueueAsync(runId, cancellationToken).ConfigureAwait(false);
                return true;
            }
        }

        return false;
    }
}
