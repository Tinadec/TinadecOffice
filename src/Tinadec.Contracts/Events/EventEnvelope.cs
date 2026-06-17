using System.Text.Json.Nodes;

namespace Tinadec.Contracts.Events;

public sealed record TinadecError(string Code, string Message, string? Detail = null);

public sealed record EventEnvelope(
    string V,
    string Type,
    string RequestId,
    string? SessionId,
    string TraceId,
    long Seq,
    DateTimeOffset Ts,
    IReadOnlyList<string> Capabilities,
    JsonObject? Payload,
    TinadecError? Error)
{
    public const string CurrentVersion = "1.0";

    public static EventEnvelope Create(
        string type,
        long seq,
        string? sessionId = null,
        JsonObject? payload = null,
        IReadOnlyList<string>? capabilities = null,
        TinadecError? error = null,
        string? requestId = null,
        string? traceId = null)
    {
        return new EventEnvelope(
            CurrentVersion,
            type,
            requestId ?? $"req_{Guid.NewGuid():N}",
            sessionId,
            traceId ?? $"trace_{Guid.NewGuid():N}",
            seq,
            DateTimeOffset.UtcNow,
            capabilities ?? Array.Empty<string>(),
            payload,
            error);
    }
}
