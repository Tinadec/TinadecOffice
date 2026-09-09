using System.Collections.Concurrent;

namespace TinadecCore.Tools;

/// <summary>
/// Process-local view of the provider calls this host currently holds. It answers
/// two questions the durable store cannot: (1) is a <c>running</c> execution row
/// owned by a live call in this process, or is it the stale remnant of a crashed
/// host; (2) which calls must be interrupted when a run is cancelled. Like the
/// terminal session registry it is intentionally volatile — a host restart clears
/// it, which is exactly what makes a leftover <c>running</c> row stale.
/// </summary>
public interface IInFlightToolCallRegistry
{
    /// <summary>True while this process holds a live provider call for the execution.</summary>
    bool IsTracked(Guid executionId);

    /// <summary>
    /// Registers one in-flight call and returns its cancellation handle. Returns
    /// null when the execution is already tracked — a defensive guard against two
    /// concurrent resumes of the same execution inside one process.
    /// </summary>
    InFlightToolCall? TryRegister(Guid runId, Guid executionId);

    /// <summary>
    /// Cancels every in-flight call owned by the run (run-control cancel path).
    /// Returns how many calls were signalled.
    /// </summary>
    int CancelForRun(Guid runId);
}

/// <summary>Cancellation handle for one tracked call. Dispose unregisters it.</summary>
public sealed class InFlightToolCall : IDisposable
{
    private readonly Action<InFlightToolCall> _unregister;
    private readonly CancellationTokenSource _source = new();

    internal InFlightToolCall(Guid runId, Guid executionId, Action<InFlightToolCall> unregister)
    {
        RunId = runId;
        ExecutionId = executionId;
        _unregister = unregister;
    }

    public Guid RunId { get; }
    public Guid ExecutionId { get; }

    /// <summary>Fires when the owning run is cancelled. Never linked to host shutdown.</summary>
    public CancellationToken Token => _source.Token;

    internal void Cancel() => _source.Cancel();

    public void Dispose()
    {
        _unregister(this);
        _source.Dispose();
    }
}

public sealed class InFlightToolCallRegistry : IInFlightToolCallRegistry
{
    private readonly ConcurrentDictionary<Guid, InFlightToolCall> _calls = new();

    public bool IsTracked(Guid executionId) => _calls.ContainsKey(executionId);

    public InFlightToolCall? TryRegister(Guid runId, Guid executionId)
    {
        var call = new InFlightToolCall(runId, executionId, Unregister);
        return _calls.TryAdd(executionId, call) ? call : null;
    }

    public int CancelForRun(Guid runId)
    {
        var owned = _calls.Values.Where(call => call.RunId == runId).ToList();
        foreach (var call in owned)
        {
            try
            {
                call.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The call completed while the cancel was being delivered.
            }
        }
        return owned.Count;
    }

    private void Unregister(InFlightToolCall call) => _calls.TryRemove(call.ExecutionId, out _);
}
