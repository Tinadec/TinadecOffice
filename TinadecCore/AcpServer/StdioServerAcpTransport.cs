using TinadecCore.Models.Harness.Acp;

namespace TinadecCore.AcpServer;

/// <summary>
/// Bridges two streams (raw process <c>stdin</c> and <c>stdout</c>, or in-memory test pipes) onto
/// <see cref="IAcpTransport"/> for server-side <see cref="JsonRpcConnection"/> hosting.
/// <para>
/// Note on stream polarity: <see cref="IAcpTransport"/> names its streams from the ACP client's view
/// of a child agent (<see cref="IAcpTransport.Stdin"/> is what <see cref="JsonRpcConnection"/> writes
/// outgoing frames to; <see cref="IAcpTransport.Stdout"/> is what <see cref="JsonRpcConnection"/> reads
/// incoming frames from). On the server side, outgoing frames go to our raw <c>stdout</c> and incoming
/// frames come from our raw <c>stdin</c>.
/// </para>
/// </summary>
internal sealed class StdioServerAcpTransport : IAcpTransport
{
    private readonly Stream _incomingInput;
    private readonly Stream _outgoingOutput;
    private readonly bool _leaveOpen;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public StdioServerAcpTransport(
        Stream incomingInput,
        Stream outgoingOutput,
        string description = "tinadec-acp-stdio-server",
        bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(incomingInput);
        ArgumentNullException.ThrowIfNull(outgoingOutput);
        _incomingInput = incomingInput;
        _outgoingOutput = outgoingOutput;
        _leaveOpen = leaveOpen;
        Description = description;
    }

    /// <summary>Stream written by <see cref="JsonRpcConnection"/>: mapped to server's raw <c>stdout</c>.</summary>
    public Stream Stdin => _outgoingOutput;

    /// <summary>Stream read by <see cref="JsonRpcConnection"/>: mapped to server's raw <c>stdin</c>.</summary>
    public Stream Stdout => _incomingInput;

    public Task Exited => _exited.Task;

    public string Description { get; }

    public void Kill()
    {
        _exited.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await _outgoingOutput.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException)
        {
        }

        _exited.TrySetResult();

        if (!_leaveOpen)
        {
            await _outgoingOutput.DisposeAsync().ConfigureAwait(false);
            await _incomingInput.DisposeAsync().ConfigureAwait(false);
        }
    }
}
