using System.Collections.Concurrent;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Tools;

/// <summary>
/// In-memory approval gate. A dispatcher parks on <see cref="WaitAsync"/> while the
/// run is in awaiting_approval; the control plane releases the waiter when the
/// approval is decided, expires, or is cancelled.
/// </summary>
public sealed class PendingApprovalGate : IToolApprovalGate
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<string>> _waiters = new();

    public Task<string> WaitAsync(Guid approvalId, CancellationToken cancellationToken = default)
    {
        var tcs = _waiters.GetOrAdd(approvalId, _ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
        return tcs.Task.WaitAsync(cancellationToken);
    }

    public void Resolve(Guid approvalId, string decision)
    {
        if (_waiters.TryRemove(approvalId, out var tcs))
        {
            tcs.TrySetResult(decision);
        }
    }

    public void Cancel(Guid approvalId)
    {
        if (_waiters.TryRemove(approvalId, out var tcs))
        {
            tcs.TrySetResult("cancelled");
        }
    }
}
