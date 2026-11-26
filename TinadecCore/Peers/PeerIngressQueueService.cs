using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.Peers;

/// <summary>
/// Per-session strict FIFO ingress queue and <c>(session_id, client_request_id)</c> idempotency coordinator.
///
/// Guarantees:
/// 1. Every local (<c>origin_device_id == ""</c>) and remote controller submission is persisted into
///    <c>peer_ingress_queue</c> with a strictly monotonic per-session <c>enqueued_seq</c> and admitted
///    in exact FIFO order via a deterministic turnstile queue.
/// 2. Duplicate submissions for a <c>(session_id, client_request_id)</c> already in
///    <c>received</c>, <c>processing</c>, or <c>processed</c> never spawn a second Run/Turn:
///    concurrent duplicates await the single in-flight ticket and return <c>idempotent_replay = true</c>,
///    while subsequent duplicates return the persisted receipt immediately.
/// </summary>
public sealed class PeerIngressQueueService : IPeerIngressQueueService
{
    private readonly IDbContextFactory<PeerDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, SessionIngressGate> _sessionGates = new(StringComparer.Ordinal);

    public PeerIngressQueueService(IDbContextFactory<PeerDbContext> dbFactory, TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<PeerIngressExecutionOutcome> ExecuteSerializedAsync(
        PeerIngressSubmitCommand command,
        Func<PeerIngressTicketContext, CancellationToken, Task<PeerIngressWorkResult>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(work);

        var sessionId = RequireNonEmpty(command.SessionId, nameof(command.SessionId));
        var clientRequestId = RequireNonEmpty(command.ClientRequestId, nameof(command.ClientRequestId));
        if (clientRequestId.Length > 128) clientRequestId = clientRequestId[..128];

        var originDeviceId = string.IsNullOrWhiteSpace(command.OriginDeviceId)
            || string.Equals(command.OriginDeviceId.Trim(), "local", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : command.OriginDeviceId.Trim();
        if (originDeviceId.Length > 128) originDeviceId = originDeviceId[..128];

        var originDisplayName = string.IsNullOrWhiteSpace(command.OriginDisplayName)
            ? (originDeviceId.Length == 0 ? "local" : originDeviceId)
            : command.OriginDisplayName.Trim();
        if (originDisplayName.Length > 256) originDisplayName = originDisplayName[..256];

        var method = string.IsNullOrWhiteSpace(command.Method) ? "session.send" : command.Method.Trim();
        if (method.Length > 64) method = method[..64];
        var payloadJson = string.IsNullOrWhiteSpace(command.PayloadJson) ? "{}" : command.PayloadJson;

        var gate = _sessionGates.GetOrAdd(sessionId, static _ => new SessionIngressGate());

        TaskCompletionSource<PeerIngressExecutionOutcome>? joinedInFlight = null;
        PeerIngressExecutionOutcome? immediateReplay = null;
        FifoTicket? ownedTicket = null;
        TaskCompletionSource<PeerIngressExecutionOutcome>? ownedCompletion = null;

        await gate.EnqueueMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (gate.InFlightByRequestId.TryGetValue(clientRequestId, out var existingInFlight))
            {
                joinedInFlight = existingInFlight;
            }
            else
            {
                var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                var (replayedRecord, createdOrResetSeq) = await SqliteTransientRetry.WriteInTransactionAsync(async ct =>
                {
                    await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
                    await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

                    var existing = await db.IngressQueue
                        .SingleOrDefaultAsync(x => x.SessionId == sessionId && x.ClientRequestId == clientRequestId, ct)
                        .ConfigureAwait(false);

                    if (existing is not null && existing.Stage is "received" or "processing" or "processed")
                    {
                        return (existing, 0L);
                    }

                    var maxDbSeq = await db.IngressQueue.AsNoTracking()
                        .Where(x => x.SessionId == sessionId)
                        .Select(x => (long?)x.EnqueuedSeq)
                        .MaxAsync(ct)
                        .ConfigureAwait(false) ?? 0L;

                    var nextSeq = Math.Max(maxDbSeq, gate.LastAssignedSeq) + 1L;
                    gate.LastAssignedSeq = nextSeq;

                    if (existing is null)
                    {
                        existing = new PeerIngressQueueRecord
                        {
                            SessionId = sessionId,
                            ClientRequestId = clientRequestId,
                            EnqueuedSeq = nextSeq,
                            OriginDeviceId = originDeviceId,
                            OriginDisplayName = originDisplayName,
                            Method = method,
                            PayloadJson = payloadJson,
                            Stage = "received",
                            RejectErrorCode = null,
                            RunId = null,
                            TurnId = null,
                            MessageId = null,
                            LastEventSeq = null,
                            ResponseJson = null,
                            ReceivedAt = nowMs,
                            UpdatedAt = nowMs
                        };
                        db.IngressQueue.Add(existing);
                    }
                    else
                    {
                        // Previously rejected row explicitly retried by user: reset to received at tail of FIFO.
                        existing.EnqueuedSeq = nextSeq;
                        existing.OriginDeviceId = originDeviceId;
                        existing.OriginDisplayName = originDisplayName;
                        existing.Method = method;
                        existing.PayloadJson = payloadJson;
                        existing.Stage = "received";
                        existing.RejectErrorCode = null;
                        existing.RunId = null;
                        existing.TurnId = null;
                        existing.MessageId = null;
                        existing.LastEventSeq = null;
                        existing.ResponseJson = null;
                        existing.ReceivedAt = nowMs;
                        existing.UpdatedAt = nowMs;
                    }

                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    await tx.CommitAsync(ct).ConfigureAwait(false);
                    return ((PeerIngressQueueRecord?)null, nextSeq);
                }, cancellationToken).ConfigureAwait(false);

                if (replayedRecord is not null)
                {
                    immediateReplay = ToOutcome(replayedRecord, idempotentReplay: true, httpStatusCode: StatusCodes200Or201(replayedRecord));
                }
                else
                {
                    ownedCompletion = new TaskCompletionSource<PeerIngressExecutionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
                    gate.InFlightByRequestId[clientRequestId] = ownedCompletion;
                    ownedTicket = gate.EnqueueTicketLocked(createdOrResetSeq, clientRequestId);
                }
            }
        }
        finally
        {
            gate.EnqueueMutex.Release();
        }

        if (immediateReplay is not null)
        {
            return immediateReplay;
        }

        if (joinedInFlight is not null)
        {
            var primaryOutcome = await joinedInFlight.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return primaryOutcome with { IdempotentReplay = true };
        }

        // Strict FIFO turnstile: wait until all lower enqueued_seq tickets in this session finish.
        var ticket = ownedTicket!;
        var completion = ownedCompletion!;
        try
        {
            await ticket.ReadyToExecute.Task.ConfigureAwait(false);

            var processingMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            await UpdateStageToProcessingAsync(sessionId, clientRequestId, processingMs).ConfigureAwait(false);

            var ctx = new PeerIngressTicketContext(
                SessionId: sessionId,
                ClientRequestId: clientRequestId,
                EnqueuedSeq: ticket.EnqueuedSeq,
                OriginDeviceId: originDeviceId,
                OriginDisplayName: originDisplayName);

            var workResult = await work(ctx, cancellationToken).ConfigureAwait(false);
            var completedMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

            var finalStage = workResult.Accepted ? "processed" : "rejected";
            var rejectCode = workResult.Accepted ? null : (workResult.RejectErrorCode ?? "rejected");

            await PersistFinalOutcomeAsync(
                sessionId,
                clientRequestId,
                finalStage,
                workResult,
                rejectCode,
                completedMs).ConfigureAwait(false);

            var outcome = new PeerIngressExecutionOutcome(
                IdempotentReplay: false,
                HttpStatusCode: workResult.HttpStatusCode,
                SessionId: sessionId,
                ClientRequestId: clientRequestId,
                EnqueuedSeq: ticket.EnqueuedSeq,
                Stage: finalStage,
                RunId: workResult.RunId,
                TurnId: workResult.TurnId,
                MessageId: workResult.MessageId,
                LastEventSeq: workResult.LastEventSeq,
                RejectErrorCode: rejectCode,
                ResponseJson: workResult.ResponseJson,
                Location: workResult.Location);

            completion.TrySetResult(outcome);
            return outcome;
        }
        catch (Exception ex)
        {
            var errMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var code = ex is OperationCanceledException ? "cancelled" : "internal_error";
            try
            {
                await PersistUnexpectedFailureAsync(sessionId, clientRequestId, code, errMs).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort failure persistence; never mask original exception or stall FIFO queue.
            }

            completion.TrySetException(ex);
            throw;
        }
        finally
        {
            await gate.CompleteTicketAndAdvanceAsync(clientRequestId, ticket).ConfigureAwait(false);
        }
    }

    public async Task<PeerIngressStatusDto> GetIngressStatusAsync(
        string sessionId,
        string clientRequestId,
        CancellationToken cancellationToken = default)
    {
        var normalizedSessionId = RequireNonEmpty(sessionId, nameof(sessionId));
        var normalizedReqId = RequireNonEmpty(clientRequestId, nameof(clientRequestId));

        return await SqliteTransientRetry.ReadAsync(async ct =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await db.IngressQueue.AsNoTracking()
                .SingleOrDefaultAsync(x => x.SessionId == normalizedSessionId && x.ClientRequestId == normalizedReqId, ct)
                .ConfigureAwait(false);

            if (row is null)
            {
                return new PeerIngressStatusDto
                {
                    Found = false,
                    SessionId = normalizedSessionId,
                    ClientRequestId = normalizedReqId
                };
            }

            return new PeerIngressStatusDto
            {
                Found = true,
                SessionId = row.SessionId,
                ClientRequestId = row.ClientRequestId,
                EnqueuedSeq = row.EnqueuedSeq,
                Stage = row.Stage,
                OriginDeviceId = row.OriginDeviceId,
                OriginDisplayName = row.OriginDisplayName,
                RunId = row.RunId,
                TurnId = row.TurnId,
                MessageId = row.MessageId,
                LastEventSeq = row.LastEventSeq,
                RejectErrorCode = row.RejectErrorCode
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateStageToProcessingAsync(string sessionId, string clientRequestId, long nowMs)
    {
        await SqliteTransientRetry.WriteInTransactionAsync(async ct =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await db.IngressQueue
                .Where(x => x.SessionId == sessionId && x.ClientRequestId == clientRequestId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(x => x.Stage, "processing")
                        .SetProperty(x => x.UpdatedAt, nowMs),
                    ct)
                .ConfigureAwait(false);
            return 0;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task PersistFinalOutcomeAsync(
        string sessionId,
        string clientRequestId,
        string finalStage,
        PeerIngressWorkResult result,
        string? rejectCode,
        long nowMs)
    {
        await SqliteTransientRetry.WriteInTransactionAsync(async ct =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await db.IngressQueue
                .SingleOrDefaultAsync(x => x.SessionId == sessionId && x.ClientRequestId == clientRequestId, ct)
                .ConfigureAwait(false);
            if (row is null) return 0;

            row.Stage = finalStage;
            row.RejectErrorCode = rejectCode;
            row.RunId = result.RunId;
            row.TurnId = result.TurnId;
            row.MessageId = result.MessageId;
            row.LastEventSeq = result.LastEventSeq;
            row.ResponseJson = result.ResponseJson;
            row.UpdatedAt = nowMs;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return 1;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task PersistUnexpectedFailureAsync(string sessionId, string clientRequestId, string errorCode, long nowMs)
    {
        await SqliteTransientRetry.WriteInTransactionAsync(async ct =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await db.IngressQueue
                .Where(x => x.SessionId == sessionId && x.ClientRequestId == clientRequestId && x.Stage != "processed")
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(x => x.Stage, "rejected")
                        .SetProperty(x => x.RejectErrorCode, errorCode)
                        .SetProperty(x => x.UpdatedAt, nowMs),
                    ct)
                .ConfigureAwait(false);
            return 0;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private static int StatusCodes200Or201(PeerIngressQueueRecord row) =>
        row.Stage == "processed" ? 201 : 200;

    private static PeerIngressExecutionOutcome ToOutcome(
        PeerIngressQueueRecord row,
        bool idempotentReplay,
        int httpStatusCode) =>
        new(
            IdempotentReplay: idempotentReplay,
            HttpStatusCode: httpStatusCode,
            SessionId: row.SessionId,
            ClientRequestId: row.ClientRequestId,
            EnqueuedSeq: row.EnqueuedSeq,
            Stage: row.Stage,
            RunId: row.RunId,
            TurnId: row.TurnId,
            MessageId: row.MessageId,
            LastEventSeq: row.LastEventSeq,
            RejectErrorCode: row.RejectErrorCode,
            ResponseJson: row.ResponseJson,
            Location: row.TurnId is not null
                ? $"/api/v1/sessions/{row.SessionId}/interactions/{row.TurnId}"
                : null);

    private static string RequireNonEmpty(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{paramName} is required.", paramName);
        return value.Trim();
    }

    private sealed class FifoTicket(long enqueuedSeq, string clientRequestId)
    {
        public long EnqueuedSeq { get; } = enqueuedSeq;
        public string ClientRequestId { get; } = clientRequestId;
        public TaskCompletionSource ReadyToExecute { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class SessionIngressGate
    {
        public SemaphoreSlim EnqueueMutex { get; } = new(1, 1);
        public Dictionary<string, TaskCompletionSource<PeerIngressExecutionOutcome>> InFlightByRequestId { get; } = new(StringComparer.Ordinal);
        public long LastAssignedSeq { get; set; }

        private readonly Queue<FifoTicket> _fifoQueue = new();
        private FifoTicket? _activeTicket;

        public FifoTicket EnqueueTicketLocked(long seq, string clientRequestId)
        {
            var ticket = new FifoTicket(seq, clientRequestId);
            if (_activeTicket is null)
            {
                _activeTicket = ticket;
                ticket.ReadyToExecute.TrySetResult();
            }
            else
            {
                _fifoQueue.Enqueue(ticket);
            }
            return ticket;
        }

        public async Task CompleteTicketAndAdvanceAsync(string clientRequestId, FifoTicket completed)
        {
            await EnqueueMutex.WaitAsync().ConfigureAwait(false);
            try
            {
                InFlightByRequestId.Remove(clientRequestId);
                if (ReferenceEquals(_activeTicket, completed))
                {
                    if (_fifoQueue.TryDequeue(out var next))
                    {
                        _activeTicket = next;
                        next.ReadyToExecute.TrySetResult();
                    }
                    else
                    {
                        _activeTicket = null;
                    }
                }
            }
            finally
            {
                EnqueueMutex.Release();
            }
        }
    }
}
