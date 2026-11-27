namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Live commit notification emitted immediately after a durable <c>run_stream</c> frame
/// commits, so in-memory followers observe appends from any writer without polling storage.
/// </summary>
public interface IRunStreamPublicationSource
{
    IDisposable SubscribeRunStream(Action<Guid, DurableRunStreamChunk> listener);
}
