using System.Collections.Concurrent;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.DmaEA;

/// <summary>
/// The live positions of every run whose stream a client is following, kept in memory so a
/// reader does not have to poll the database to see a token that was produced a moment ago.
///
/// Why this exists: <see cref="FullDuplexRunCoordinator.FollowAsync"/> used to re-read
/// <c>run_stream</c> every 150 ms, so durable storage sat on the render path. A token reached the
/// user only after it had been written, content-hashed and committed, and the poll interval was
/// the floor on how live the stream felt. The durable write is still the record of truth — this
/// type only removes it from the path the user waits on.
///
/// Two properties matter and are the whole design:
/// <list type="bullet">
/// <item>A frame published here is already durable. The publisher calls this after the write
/// returns, so a reader can never show something a crash would lose, and the terminal frame and
/// the run's terminal status are observed in the same order.</item>
/// <item>It is only a cache of positions, never the answer. Dropping everything (a restart, an
/// eviction) costs nothing: the reader falls back to replaying the durable stream from its
/// cursor, which is exactly what recovery already does.</item>
/// </list>
/// </summary>
internal sealed class RunStreamProjection
{
    /// <summary>
    /// How many frames one run keeps live. A reader that falls further behind than this replays
    /// from storage instead; the bound keeps a run that nobody follows from growing a list for
    /// the life of the process.
    /// </summary>
    private const int MaxFramesPerRun = 4096;
    private const int MaxTrackedRuns = 256;
    private static readonly TimeSpan SealedLingerDelay = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<Guid, RunBuffer> _runs = new();

    /// <summary>
    /// Read-only observer hook for governance-layer sidecar consumers (e.g. LiveExplainerService).
    /// Must never block or throw onto the execution stream path.
    /// </summary>
    internal event Action<RunStreamChunk>? ChunkObserved;

    /// <summary>Read-only observer hook raised when a run stream seals.</summary>
    internal event Action<Guid>? RunSealed;

    /// <summary>
    /// Starts retaining a run's frames. Called by a follower as it attaches so a run that is
    /// producing output has somewhere to publish to even before the first frame arrives.
    /// </summary>
    public void Track(Guid runId)
    {
        PruneIfNeeded(runId);
        _runs.GetOrAdd(runId, static _ => new RunBuffer());
    }

    public bool IsSealed(Guid runId) => _runs.TryGetValue(runId, out var buffer) && buffer.Sealed;

    /// <summary>
    /// Publishes one durable frame. Called only after the durable append returned, so the frame
    /// is already recoverable if the process dies on the next instruction.
    /// </summary>
    public void Publish(RunStreamChunk chunk)
    {
        PruneIfNeeded(chunk.RunId);
        var buffer = _runs.GetOrAdd(chunk.RunId, static _ => new RunBuffer());
        TaskCompletionSource<bool>? signalToRelease = null;
        lock (buffer.Frames)
        {
            if (buffer.HasDroppedPrefix && chunk.Seq < buffer.FirstRetainedSeq)
                return;

            // The same frame can be published twice when a write is retried and the idempotency
            // key returns the row that already existed; a reader must not see it twice.
            if (buffer.Frames.Count > 0 && chunk.Seq <= buffer.Frames[^1].Seq)
            {
                if (chunk.Seq == buffer.Frames[^1].Seq || buffer.Frames.Any(frame => frame.Seq == chunk.Seq))
                    return;
                // Out-of-order arrival (two writers racing on one run): insert in order.
                var at = buffer.Frames.FindIndex(frame => frame.Seq > chunk.Seq);
                buffer.Frames.Insert(at < 0 ? buffer.Frames.Count : at, chunk);
            }
            else
            {
                buffer.Frames.Add(chunk);
            }

            if (buffer.Frames.Count > MaxFramesPerRun)
            {
                buffer.Frames.RemoveRange(0, buffer.Frames.Count - MaxFramesPerRun);
                buffer.HasDroppedPrefix = true;
            }
            if (buffer.Frames.Count > 0)
            {
                buffer.FirstRetainedSeq = buffer.Frames[0].Seq;
            }

            if (!buffer.Sealed)
            {
                signalToRelease = buffer.Signal;
                buffer.Signal = CreateSignal();
            }
        }
        signalToRelease?.TrySetResult(true);
        if (!LiveExplainer.ObserverRunScope.IsCurrentObserverRun)
        {
            try { ChunkObserved?.Invoke(chunk); } catch { /* observer sidecar never faults execution */ }
        }
    }

    /// <summary>
    /// Waits until the run has frames past <paramref name="cursor"/>, the run is sealed, or
    /// <paramref name="cancellationToken"/> fires. Returns whether frames are ready.
    /// </summary>
    public async Task<bool> WaitForFramesAsync(Guid runId, long cursor, CancellationToken cancellationToken)
    {
        if (!_runs.TryGetValue(runId, out var buffer)) return false;
        Task waitTask;
        lock (buffer.Frames)
        {
            if (buffer.HasFramesPastUnlocked(cursor) || buffer.Sealed) return true;
            waitTask = buffer.Signal.Task;
        }
        // A bounded wait, not an open one: this is a long-lived HTTP response and must stay
        // responsive to a run that stops producing without sealing (a stuck provider), which it
        // handles by falling back to the durable stream.
        try
        {
            await waitTask.WaitAsync(TimeSpan.FromMilliseconds(FollowWaitMilliseconds), cancellationToken).ConfigureAwait(false);
            return buffer.HasFramesPast(cursor) || buffer.Sealed;
        }
        catch (TimeoutException)
        {
            return buffer.HasFramesPast(cursor) || buffer.Sealed;
        }
    }

    /// <summary>Frames past <paramref name="cursor"/>, in sequence order.</summary>
    public IReadOnlyList<RunStreamChunk> Since(Guid runId, long cursor)
    {
        if (!_runs.TryGetValue(runId, out var buffer)) return [];
        lock (buffer.Frames)
        {
            // A reader whose cursor fell behind the retained window gets nothing here and
            // replays storage instead: an incomplete answer is worse than a slower one.
            if (buffer.Frames.Count > 0 && cursor < buffer.Frames[0].Seq - 1) return [];
            return buffer.Frames.Where(frame => frame.Seq > cursor).ToArray();
        }
    }

    /// <summary>
    /// True when this buffer can answer a reader starting at <paramref name="cursor"/> on its
    /// own: it holds at least one frame, and the retained window still reaches back to the
    /// cursor so nothing between that position and the newest frame was dropped.
    ///
    /// An empty buffer is deliberately not covering. It cannot prove there are no earlier
    /// frames in storage — a follower that attaches before the run produces anything, or after
    /// a restart, would otherwise be told "nothing to send" and skip the durable history it
    /// missed. The caller replays storage in that case and switches to live on a later pass.
    /// </summary>
    public bool Covers(Guid runId, long cursor)
    {
        if (!_runs.TryGetValue(runId, out var buffer)) return false;
        lock (buffer.Frames)
            return buffer.Frames.Count > 0 && cursor >= buffer.Frames[0].Seq - 1;
    }

    /// <summary>
    /// True when the run has frames past <paramref name="cursor"/> without consulting storage.
    /// A follower uses this to switch from replay to live as soon as the run starts producing.
    /// </summary>
    public bool HasFramesPast(Guid runId, long cursor)
        => _runs.TryGetValue(runId, out var buffer) && buffer.HasFramesPast(cursor);

    /// <summary>
    /// Marks the run finished and forgets it shortly after, so a reader still draining the tail
    /// sees the terminal frame and a later follower starts from storage.
    /// </summary>
    public void Seal(Guid runId)
    {
        if (!_runs.TryGetValue(runId, out var buffer)) return;
        TaskCompletionSource<bool>? signalToRelease = null;
        bool sealedNow = false;
        lock (buffer.Frames)
        {
            if (!buffer.Sealed)
            {
                buffer.Sealed = true;
                buffer.SealedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
                sealedNow = true;
                signalToRelease = buffer.Signal;
                // Replace with an already-completed signal once sealed so no future waiter can ever hang.
                buffer.Signal = CreateCompletedSignal();
            }
        }
        signalToRelease?.TrySetResult(true);
        if (sealedNow)
        {
            _ = ForgetAfterLingerAsync(runId, buffer);
            if (!LiveExplainer.ObserverRunScope.IsCurrentObserverRun)
            {
                try { RunSealed?.Invoke(runId); } catch { /* observer sidecar never faults execution */ }
            }
        }
    }

    /// <summary>Drops a run entirely (its terminal status is already durable).</summary>
    public void Forget(Guid runId) => _runs.TryRemove(runId, out _);

    private async Task ForgetAfterLingerAsync(Guid runId, RunBuffer buffer)
    {
        try
        {
            await Task.Delay(SealedLingerDelay).ConfigureAwait(false);
            _runs.TryRemove(new KeyValuePair<Guid, RunBuffer>(runId, buffer));
        }
        catch
        {
            // Process shutting down or timer ignored; eviction is best-effort.
        }
    }

    private void PruneIfNeeded(Guid incomingRunId)
    {
        if (_runs.Count < MaxTrackedRuns) return;
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-2).UtcTicks;
        foreach (var pair in _runs)
        {
            if (pair.Key == incomingRunId) continue;
            if (pair.Value.Sealed && pair.Value.SealedAtUtcTicks <= cutoff)
            {
                _runs.TryRemove(pair);
            }
        }
    }

    private static TaskCompletionSource<bool> CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<bool> CreateCompletedSignal()
    {
        var tcs = CreateSignal();
        tcs.TrySetResult(true);
        return tcs;
    }

    private const int FollowWaitMilliseconds = 250;

    private sealed class RunBuffer
    {
        public readonly List<RunStreamChunk> Frames = [];
        public TaskCompletionSource<bool> Signal = CreateSignal();
        public volatile bool Sealed;
        public long SealedAtUtcTicks;
        public bool HasDroppedPrefix;
        public long FirstRetainedSeq;

        public bool HasFramesPast(long cursor)
        {
            lock (Frames) return HasFramesPastUnlocked(cursor);
        }

        public bool HasFramesPastUnlocked(long cursor) =>
            Frames.Count > 0 && Frames[^1].Seq > cursor;
    }
}
