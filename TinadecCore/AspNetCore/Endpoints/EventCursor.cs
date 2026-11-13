namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// The live event feed's resume cursor. Sequences are allocated per run, so a single number
/// cannot address a session that has more than one run: a new run restarts at one and a scalar
/// cursor either replays it twice or skips it entirely. A cursor is therefore a vector of
/// <c>run:sequence</c> pairs. Event frames carry the single pair they advance (model deltas are
/// high frequency and must stay small); heartbeats carry the whole vector so a subscriber that
/// merges both always holds a complete watermark set.
/// </summary>
internal static class EventCursor
{
    internal static bool TryParse(string text, out Dictionary<Guid, long> watermarks)
    {
        watermarks = new Dictionary<Guid, long>();
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var segment in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = segment.Split(':');
            if (parts.Length != 2
                || !Guid.TryParseExact(parts[0], "N", out var runId)
                || !long.TryParse(parts[1], out var sequence)
                || sequence < 0)
                return false;
            watermarks[runId] = sequence;
        }
        return watermarks.Count > 0;
    }

    internal static string EncodeEvent(Guid runId, long sequence) => $"{runId:N}:{sequence}";

    internal static string EncodeVector(IReadOnlyDictionary<Guid, long> watermarks) =>
        string.Join(',', watermarks.Select(pair => EncodeEvent(pair.Key, pair.Value)));
}