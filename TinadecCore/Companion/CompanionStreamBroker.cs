using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;

namespace TinadecCore.Companion;

public sealed class CompanionStreamBroker : ICompanionStreamBroker, IDisposable
{
    public const string DeltaEvent = "companion.delta";
    public const string ToolCallEvent = "companion.tool_call";
    public const string ProposalEvent = "companion.proposal";
    public const string StageCueEvent = "companion.stage_cue";
    public const string WorkSessionEvent = "companion.work_session";

    private static readonly JsonSerializerOptions SnakeCase = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly CompanionStreamSignal ActivityWakeSignal = new(IsActivityWake: true);

    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<long, Channel<CompanionStreamSignal>>> _tenants = new();
    private long _nextSubscriberId;

    public CompanionStreamBroker()
    {
        CompanionActivities.ActivitiesCommitted += NotifyActivityCommitted;
    }

    public void Dispose()
    {
        CompanionActivities.ActivitiesCommitted -= NotifyActivityCommitted;
        foreach (var bucket in _tenants.Values)
        {
            foreach (var channel in bucket.Values)
            {
                channel.Writer.TryComplete();
            }
        }
        _tenants.Clear();
    }

    public void PublishDelta(
        Guid tenantId,
        Guid runId,
        Guid companionId,
        Guid conversationId,
        string delta,
        int seq)
    {
        if (string.IsNullOrEmpty(delta)) return;
        PublishEphemeral(tenantId, DeltaEvent, new
        {
            run_id = runId,
            companion_id = companionId,
            conversation_id = conversationId,
            delta,
            seq
        });
    }

    public void PublishToolCall(
        Guid tenantId,
        Guid runId,
        Guid companionId,
        Guid conversationId,
        string callId,
        string toolName,
        string status,
        string? argumentsPreview = null,
        string? resultPreview = null)
    {
        PublishEphemeral(tenantId, ToolCallEvent, new
        {
            run_id = runId,
            companion_id = companionId,
            conversation_id = conversationId,
            call_id = callId,
            tool_name = toolName,
            status,
            arguments_preview = argumentsPreview,
            result_preview = resultPreview
        });
    }

    public void PublishProposal(
        Guid tenantId,
        Guid companionId,
        Guid? conversationId,
        string kind,
        Guid proposalId,
        string action,
        string status,
        string summary,
        object? payload = null)
    {
        PublishEphemeral(tenantId, ProposalEvent, new
        {
            companion_id = companionId,
            conversation_id = conversationId,
            kind,
            proposal_id = proposalId,
            action,
            status,
            summary,
            payload
        });
    }

    public void PublishStageCue(
        Guid tenantId,
        Guid companionId,
        Guid? conversationId,
        Guid? runId,
        string emotion,
        bool speaking,
        double intensity = 0.6,
        string? gesture = null)
    {
        PublishEphemeral(tenantId, StageCueEvent, new
        {
            companion_id = companionId,
            conversation_id = conversationId,
            run_id = runId,
            emotion,
            speaking,
            intensity = Math.Clamp(intensity, 0d, 1d),
            gesture
        });
    }

    public void PublishWorkSession(
        Guid tenantId,
        Guid? companionId,
        Guid? conversationId,
        object payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!_tenants.TryGetValue(tenantId, out var bucket) || bucket.IsEmpty) return;

        var merged = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["companion_id"] = companionId,
            ["conversation_id"] = conversationId
        };

        var element = JsonSerializer.SerializeToElement(payload, SnakeCase);
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Name is "companion_id" or "conversation_id" && prop.Value.ValueKind == JsonValueKind.Null)
                    continue;
                merged[prop.Name] = prop.Value;
            }
        }
        else
        {
            merged["payload"] = element;
        }

        PublishEphemeral(tenantId, WorkSessionEvent, merged);
    }

    public void NotifyActivityCommitted(Guid tenantId) =>
        Broadcast(tenantId, ActivityWakeSignal);

    public ICompanionStreamSubscription Subscribe(Guid tenantId)
    {
        var id = Interlocked.Increment(ref _nextSubscriberId);
        var channel = Channel.CreateBounded<CompanionStreamSignal>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        var bucket = _tenants.GetOrAdd(tenantId, static _ => new ConcurrentDictionary<long, Channel<CompanionStreamSignal>>());
        bucket[id] = channel;
        return new Subscription(this, tenantId, id, channel);
    }

    private void PublishEphemeral(Guid tenantId, string eventName, object payload)
    {
        if (!_tenants.TryGetValue(tenantId, out var bucket) || bucket.IsEmpty) return;
        var json = JsonSerializer.Serialize(payload, SnakeCase);
        var signal = new CompanionStreamSignal(IsActivityWake: false, EventName: eventName, DataJson: json);
        foreach (var channel in bucket.Values)
        {
            channel.Writer.TryWrite(signal);
        }
    }

    private void Broadcast(Guid tenantId, CompanionStreamSignal signal)
    {
        if (!_tenants.TryGetValue(tenantId, out var bucket) || bucket.IsEmpty) return;
        foreach (var channel in bucket.Values)
        {
            channel.Writer.TryWrite(signal);
        }
    }

    private void Unsubscribe(Guid tenantId, long subscriberId)
    {
        if (!_tenants.TryGetValue(tenantId, out var bucket)) return;
        if (bucket.TryRemove(subscriberId, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    private sealed class Subscription(
        CompanionStreamBroker owner,
        Guid tenantId,
        long subscriberId,
        Channel<CompanionStreamSignal> channel) : ICompanionStreamSubscription
    {
        private int _disposed;

        public async ValueTask<CompanionStreamSignal?> ReadOrTimeoutAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (channel.Reader.TryRead(out var immediate))
                return immediate;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try
            {
                if (await channel.Reader.WaitToReadAsync(timeoutCts.Token).ConfigureAwait(false)
                    && channel.Reader.TryRead(out var next))
                {
                    return next;
                }
                return null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Unsubscribe(tenantId, subscriberId);
            }
        }
    }
}
