using System.Runtime.CompilerServices;
using System.Text;

namespace TinadecCore.DmaEA.CliRuntime;

/// <summary>
/// Minimal client-side SSE parser: reads lines, accumulates multi-line <c>data:</c> fields,
/// and yields a record per blank-line-delimited event. Comments and unknown fields are skipped.
/// </summary>
internal static class SseReader
{
    public static async IAsyncEnumerable<SseEvent> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var eventName = (string?)null;
        var data = new StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return new SseEvent(eventName, data.ToString());
                    eventName = null;
                    data.Clear();
                }
                continue;
            }
            if (line.StartsWith(':')) continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..].TrimStart(' ');
            if (field == "event") eventName = value;
            else if (field == "data")
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(value);
            }
        }
        if (data.Length > 0) yield return new SseEvent(eventName, data.ToString());
    }

    internal sealed record SseEvent(string? EventName, string Data);
}
