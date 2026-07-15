namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Manages agent memory scope, retention policy, and provenance.
/// Wraps MAF AgentSession serialization and ChatHistoryProvider.
/// </summary>
public interface IMemoryStore
{
    Task<MemoryEntry[]> RetrieveAsync(
        string sessionId,
        string query,
        int maxResults,
        CancellationToken cancellationToken = default);

    Task StoreAsync(
        string sessionId,
        MemoryEntry entry,
        CancellationToken cancellationToken = default);
}

public sealed class MemoryEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public float Score { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyDictionary<string, string> Provenance { get; init; } = new Dictionary<string, string>();
}
