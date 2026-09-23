using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Skills;

/// <summary>
/// The one way a market adapter reads an external document: through the Tool Provider's reserved
/// <c>#fetch</c> control tool.
///
/// Why the read goes out through the tool process rather than an <see cref="HttpClient"/> in
/// Core: the provider owns the server-side request forgery guard, and it is the one that can
/// apply it where it matters — inside the connect callback, against the address it is about to
/// dial. A second HTTP stack in Core would be a second policy to keep in sync and to audit, and
/// the first thing wrong with it would be that it disagreed with the tool layer.
///
/// This lives outside any single adapter because the three ways a fetch can not-produce-a-body
/// have to be told apart once and the same way for every source: the guard refused the target
/// (<see cref="Page.Blocked"/> — Core asked for something it should not have), the target could
/// not be reached, or an answer came back with nothing readable in it. A refresh outcome is
/// reported to a human, and "the market was unreachable" must not be spelled the same way as
/// "this source points somewhere we may not connect to".
/// </summary>
internal static class MarketFetch
{
    /// <summary>Reserved control tool; never offered to a model in a manifest.</summary>
    internal const string ToolId = "#fetch";

    /// <summary>Ask for the provider's own ceiling: a truncated JSON body is an unusable body.</summary>
    internal const int MaxBytes = 8 * 1024 * 1024;

    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    internal sealed record Page(string? Body, string? Error, bool Blocked = false);

    internal static async Task<Page> FetchPageAsync(
        IToolProvider provider,
        string workspaceRoot,
        string url,
        CancellationToken cancellationToken)
    {
        JsonElement result;
        try
        {
            var response = await provider.CallAsync(
                workspaceRoot,
                new ToolWireRequestDto
                {
                    ToolId = ToolId,
                    // No run owns a market read; the provider only echoes this on wire events.
                    SessionId = "market-refresh",
                    Approved = false,
                    Params = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                    {
                        ["url"] = url,
                        ["max_bytes"] = MaxBytes,
                        ["timeout_ms"] = (int)Timeout.TotalMilliseconds,
                    })
                },
                Timeout + TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccess)
                return new Page(null, response.Error ?? "The tool provider rejected the fetch.");

            if (response.Result is not { } payload || payload.ValueKind != JsonValueKind.Object)
                return new Page(null, $"{ToolId} returned no object payload.");

            result = payload;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Page(null, ex.Message);
        }

        if (result.TryGetProperty("blocked", out var blocked) && blocked.ValueKind == JsonValueKind.True)
            return new Page(null, Text(result, "error") ?? "The egress guard refused this target.", true);

        if (!result.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            return new Page(null, Text(result, "error") ?? $"Fetching {url} did not succeed.");

        var body = Text(result, "body");
        if (string.IsNullOrEmpty(body))
            return new Page(null, $"Fetching {url} returned no readable body.");

        return new Page(body, null, false);
    }

    /// <summary>First named property holding a string, in the order given.</summary>
    internal static string? Text(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }

    /// <summary>
    /// Length-bound without cutting mid-surrogate-pair, and null for empty rather than a string of
    /// nothing — an absent description and a blank one should not be two wire shapes. External text
    /// gets its ceiling here, at the boundary, so no downstream renderer has to trust a length.
    /// </summary>
    internal static string? Bound(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (trimmed.Length <= max)
            return trimmed;

        var cut = trimmed[..max];
        if (char.IsHighSurrogate(cut[^1]))
            cut = cut[..^1];

        return cut;
    }
}
