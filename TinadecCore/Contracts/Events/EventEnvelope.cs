using System.Text.Json.Serialization;

namespace TinadecCore.Contracts.Events;

/// <summary>
/// Versioned event envelope. Uses snake_case at the API boundary.
/// Does not expose MAF or F# types.
/// </summary>
public sealed class EventEnvelope
{
    public const string SchemaVersion = "1.0";

    public string Version { get; init; } = SchemaVersion;

    [JsonPropertyName("event_id")]
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("event_type")]
    public string EventType { get; init; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("run_id")]
    public string? RunId { get; init; }

    [JsonPropertyName("payload")]
    public IReadOnlyDictionary<string, object?> Payload { get; init; } = new Dictionary<string, object?>();
}
