namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// The hard-insert signal (todo D2). The steering itself is durable — a context patch the endpoint
/// records first — so this only makes it take effect now instead of at the run's next boundary: the
/// run's in-flight model calls on this host are cut off and redone with the steering in view, and
/// tool calls the model asked for but that have not started are not run.
///
/// A tool call already running is never cut: a call that changes the workspace would end with an
/// unknown outcome and park the run for a human, which is the opposite of steering it. It finishes and
/// the steering applies right after it.
///
/// Process-local on purpose, like <see cref="IRunInFlightToolCancellation"/>: a run owned by another
/// host is not reached, and its next model call reads the steering anyway (the soft insert).
/// </summary>
public interface IRunInterrupts
{
    /// <summary>
    /// Asks the run to stop what it is doing and take <paramref name="request"/> into account. Returns
    /// how many of its model calls this host cut off. The request stays pending until the engine acts on
    /// it, so a model call that starts before then is cut off as well.
    /// </summary>
    int Request(Guid runId, RunInterruptRequest request);

    /// <summary>Registers one model call of the run. Its token fires when the run is interrupted.</summary>
    RunInterruptScope Track(Guid runId);

    /// <summary>Takes the run's pending request, if any: the engine has acted on it.</summary>
    RunInterruptRequest? TakePending(Guid runId);

    /// <summary>The run's pending request without taking it.</summary>
    RunInterruptRequest? PeekPending(Guid runId);
}

/// <summary>What interrupted the run: the steering patch and its text, for the interrupted agent's record.</summary>
public sealed record RunInterruptRequest(Guid PatchId, string Content, DateTimeOffset RequestedAt);

/// <summary>One tracked model call. Dispose when the call ends.</summary>
public sealed class RunInterruptScope : IDisposable
{
    private readonly CancellationTokenSource _source = new();
    private readonly Action<RunInterruptScope> _release;
    private RunInterruptRequest? _request;

    internal RunInterruptScope(Guid runId, Action<RunInterruptScope> release)
    {
        RunId = runId;
        _release = release;
    }

    public Guid RunId { get; }

    /// <summary>Fires when the run is interrupted. Never linked to host shutdown or run cancellation.</summary>
    public CancellationToken Token => _source.Token;

    /// <summary>The request that cut this call off; null while it was not interrupted.</summary>
    public RunInterruptRequest? Request => Volatile.Read(ref _request);

    public bool Interrupted => Request is not null;

    internal void Interrupt(RunInterruptRequest request)
    {
        Volatile.Write(ref _request, request);
        try
        {
            _source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The call ended while the interrupt was being delivered.
        }
    }

    public void Dispose()
    {
        _release(this);
        _source.Dispose();
    }
}

/// <summary>
/// The process-local <see cref="IRunInterrupts"/>. Volatile by design: a host restart forgets pending
/// requests, and the restarted run reads the durable steering at its first model call anyway.
/// </summary>
public sealed class RunInterruptRegistry : IRunInterrupts
{
    /// <summary>A request nobody took within this long belongs to a run that is no longer here.</summary>
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, RunState> _runs = [];

    private sealed class RunState
    {
        public List<RunInterruptScope> Calls { get; } = [];
        public RunInterruptRequest? Pending { get; set; }
    }

    public int Request(Guid runId, RunInterruptRequest request)
    {
        RunInterruptScope[] calls;
        lock (_gate)
        {
            Prune(request.RequestedAt);
            var state = StateOf(runId);
            state.Pending = request;
            calls = [.. state.Calls];
        }
        foreach (var call in calls) call.Interrupt(request);
        return calls.Length;
    }

    public RunInterruptScope Track(Guid runId)
    {
        var scope = new RunInterruptScope(runId, Release);
        RunInterruptRequest? pending;
        lock (_gate)
        {
            var state = StateOf(runId);
            state.Calls.Add(scope);
            pending = state.Pending;
        }
        // A call that starts after the request but before the engine acted on it may have built its
        // prompt without the steering: it is cut off too, and redone once the engine has taken it.
        if (pending is not null) scope.Interrupt(pending);
        return scope;
    }

    public RunInterruptRequest? TakePending(Guid runId)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var state) || state.Pending is not { } pending) return null;
            state.Pending = null;
            if (state.Calls.Count == 0) _runs.Remove(runId);
            return pending;
        }
    }

    public RunInterruptRequest? PeekPending(Guid runId)
    {
        lock (_gate) return _runs.TryGetValue(runId, out var state) ? state.Pending : null;
    }

    private RunState StateOf(Guid runId)
    {
        if (!_runs.TryGetValue(runId, out var state)) _runs[runId] = state = new RunState();
        return state;
    }

    private void Release(RunInterruptScope scope)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(scope.RunId, out var state)) return;
            state.Calls.Remove(scope);
            if (state.Calls.Count == 0 && state.Pending is null) _runs.Remove(scope.RunId);
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (runId, state) in _runs.Where(pair => pair.Value.Calls.Count == 0
            && pair.Value.Pending is { } pending && now - pending.RequestedAt > PendingLifetime).ToArray())
            _runs.Remove(runId);
    }
}
