using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using TinadecTools.Abstractions;
using TinadecTools.Tools;

namespace TinadecTools.Tools.Web;

public sealed class WebFetchArgs
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("max_bytes")] public int MaxBytes { get; set; }
    [JsonPropertyName("max_chars")] public int MaxChars { get; set; }
    [JsonPropertyName("timeout_ms")] public int TimeoutMs { get; set; }
    [JsonPropertyName("confirm_fetch")] public string? ConfirmFetch { get; set; }
}

public sealed class WebFetchResult
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("blocked")] public bool Blocked { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("status_code")] public int StatusCode { get; set; }
    [JsonPropertyName("media_type")] public string? MediaType { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }
    [JsonPropertyName("byte_count")] public int ByteCount { get; set; }
    [JsonPropertyName("declared_bytes")] public long DeclaredBytes { get; set; } = -1;
    [JsonPropertyName("redirect_count")] public int RedirectCount { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(WebFetchArgs))]
[JsonSerializable(typeof(WebFetchResult))]
internal partial class WebFetchToolJsonContext : JsonSerializerContext { }

public static class WebFetchTool
{
    private const string UserAgent = "TinadecOffice-agent/1.0 (web_fetch; read-only)";
    private const string AcceptHeader = "text/markdown, text/plain;q=0.9, text/html;q=0.8, application/xhtml+xml;q=0.7, application/json;q=0.6, */*;q=0.1";

    public const int DefaultMaxBytes = 256 * 1024;
    public const int MaxCeilingBytes = 4 * 1024 * 1024;
    public const int DefaultMaxChars = 12_000;
    public const int MaxCeilingChars = 60_000;
    public const int DefaultTimeoutMs = 15_000;
    public const int MinTimeoutMs = 1_000;
    public const int MaxTimeoutMs = 60_000;

    [ToolFunction("web_fetch", RequiresApproval = true, MutatesWorkspace = false, ConfirmationFields = ["confirm_fetch"],
        Description = "Fetch one public http(s) URL into text (approval-gated; confirm_fetch must be non-empty to run). Only text-like media types get a body, capped at max_bytes/max_chars with truncated=true when cut. Private, loopback, link-local (including cloud metadata) and tunnel addresses come back blocked=true — that is policy, not a network failure, so retrying the same target will not help. A 4xx/5xx keeps success=false but still returns the readable body: report status_code rather than guessing whether the page exists.")]
    public static ValueTask<WebFetchResult> FetchAsync(WebFetchArgs args, CancellationToken cancellationToken) =>
        FetchAsync(args, null, cancellationToken);

    /// <summary>
    /// <paramref name="resolve"/> is the DNS boundary and <paramref name="handlerFactory"/>
    /// the transport boundary: production passes null for both and gets
    /// <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/> plus the
    /// guarded connect callback, a test passes a name table and a canned handler so
    /// rebinding, ceilings and redirect revalidation can be asserted against hosts
    /// that do not exist.
    /// </summary>
    internal static async ValueTask<WebFetchResult> FetchAsync(
        WebFetchArgs args,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve,
        CancellationToken cancellationToken,
        Func<HttpMessageHandler>? handlerFactory = null)
    {
        ToolConfirmations.Require(args.ConfirmFetch, nameof(args.ConfirmFetch));

        if (!WebFetchGuard.TryBuildTarget(args.Url, out var initial, out var urlError))
            return Refused(urlError!);

        var target = initial!;

        var maxBytes = Clamp(args.MaxBytes, 1, MaxCeilingBytes, DefaultMaxBytes);
        var maxChars = Clamp(args.MaxChars, 1, MaxCeilingChars, DefaultMaxChars);
        var timeoutMs = Clamp(args.TimeoutMs, MinTimeoutMs, MaxTimeoutMs, DefaultTimeoutMs);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeoutMs);

        var resolver = resolve ?? ((host, token) => Dns.GetHostAddressesAsync(host, token));
        using var client = new HttpClient(handlerFactory?.Invoke() ?? CreateGuardedHandler(resolver), disposeHandler: true)
        {
            // One budget for the whole call, redirects included, so a chain of
            // hops cannot multiply the timeout the caller asked for.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var redirects = 0;
        var note = (string?)null;
        while (true)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, target);
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                request.Headers.TryAddWithoutValidation("Accept", AcceptHeader);

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                var mediaType = WebFetchContent.MediaTypeOf(response.Content.Headers.ContentType?.MediaType);
                var declared = response.Content.Headers.ContentLength ?? -1;

                if (status is >= 300 and < 400)
                {
                    if (redirects >= WebFetchGuard.MaxRedirections)
                        return Refused($"'{target}' redirected more than {WebFetchGuard.MaxRedirections} times; web_fetch stopped instead of following the chain.", target, status, redirects);

                    if (response.Headers.Location is not { } location)
                        return Refused($"HTTP {status} carried no Location, so the redirect could not be followed.", target, status, redirects);

                    if (!WebFetchGuard.TryBuildTarget(new Uri(target, location).ToString(), out var built, out var redirectError))
                        return Refused($"Redirect to '{location}' refused: {redirectError}", target, status, redirects);

                    var hop = built!;
                    if (!string.Equals(hop.Host, target.Host, StringComparison.OrdinalIgnoreCase))
                        note = AppendNote(note, $"followed a redirect to {hop.Scheme}://{hop.Host}");

                    if (string.Equals(hop.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(target.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    {
                        note = AppendNote(note, "a redirect downgraded this request to plain http");
                    }

                    target = hop;
                    redirects++;
                    continue;
                }

                var encoding = WebFetchContent.ResolveEncoding(response.Content.Headers.ContentType?.ToString(), out var charsetNote);
                note = AppendNote(note, charsetNote);

                var (bytes, byteTruncated) = await ReadCappedAsync(response, maxBytes, budget.Token).ConfigureAwait(false);
                if (mediaType.Length == 0 || !WebFetchContent.IsTextual(mediaType))
                {
                    return new WebFetchResult
                    {
                        Success = false,
                        Error = $"web_fetch returns text only: this response is {(mediaType.Length == 0 ? "an unlabelled media type" : mediaType)} ({bytes.Length} byte(s)) and its body was not captured. Ask for a text or JSON endpoint instead.",
                        Url = target.ToString(),
                        StatusCode = status,
                        MediaType = mediaType,
                        ByteCount = bytes.Length,
                        DeclaredBytes = declared,
                        RedirectCount = redirects,
                        Note = note,
                    };
                }

                var text = WebFetchContent.IsHtml(mediaType)
                    ? WebFetchContent.ToText(encoding.GetString(bytes))
                    : encoding.GetString(bytes);
                text = WebFetchContent.CapChars(text, maxChars, out var charTruncated);

                return new WebFetchResult
                {
                    Success = status < 400,
                    Error = status >= 400 ? $"HTTP {status} from {target.Host}" : null,
                    Url = target.ToString(),
                    StatusCode = status,
                    MediaType = mediaType,
                    Text = text,
                    Truncated = byteTruncated || charTruncated,
                    ByteCount = bytes.Length,
                    DeclaredBytes = declared,
                    RedirectCount = redirects,
                    Note = AppendNote(note, TruncationNote(byteTruncated, charTruncated, maxBytes, maxChars)),
                };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new WebFetchResult
                {
                    Success = false,
                    Error = $"web_fetch gave up on '{target}' after {timeoutMs} ms; the host may be slow or unreachable, and timeout_ms accepts up to {MaxTimeoutMs}.",
                    Url = target.ToString(),
                    RedirectCount = redirects,
                    Note = note,
                };
            }
            catch (Exception ex)
            {
                var refusal = FindRefusal(ex);
                if (refusal is not null)
                    return Refused(refusal.Reason!, target, 0, redirects);

                return new WebFetchResult
                {
                    Success = false,
                    Error = $"'{target}' could not be fetched: {ex.Message}",
                    Url = target.ToString(),
                    RedirectCount = redirects,
                    Note = note,
                };
            }
        }
    }

    /// <summary>
    /// The address check runs here, inside the connect callback, so a name that
    /// resolves publicly on the first lookup and privately on the second still
    /// cannot open a socket to an internal address.
    /// </summary>
    private static SocketsHttpHandler CreateGuardedHandler(Func<string, CancellationToken, Task<IPAddress[]>> resolver) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;
            var port = context.DnsEndPoint.Port;
            var allowed = await WebFetchGuard.ResolveAllowedAsync(host, resolver, cancellationToken).ConfigureAwait(false);

            Socket? opened = null;
            Exception? last = null;
            foreach (var address in allowed)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                };

                try
                {
                    await socket.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
                    opened = socket;
                    break;
                }
                catch (Exception ex)
                {
                    last = ex;
                    socket.Dispose();
                }
            }

            if (opened is null)
            {
                throw new WebFetchRefusedException(
                    $"'{host}:{port}' resolved to {string.Join(", ", allowed)}, but no connection could be opened: {last?.Message ?? "every candidate address refused"}");
            }

            return new NetworkStream(opened, ownsSocket: true);
        },
    };

    private static async Task<(byte[] Bytes, bool Truncated)> ReadCappedAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[maxBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var got = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken).ConfigureAwait(false);
            if (got == 0)
                break;

            read += got;
        }

        if (read <= maxBytes)
            return (buffer[..read], false);

        // One byte over the ceiling is all the evidence needed to say "there was
        // more"; the extra byte is dropped rather than reported as fetched.
        return (buffer[..maxBytes], true);
    }

    private static WebFetchResult Refused(string reason) => new()
    {
        Success = false,
        Blocked = true,
        Error = reason,
    };

    private static WebFetchResult Refused(string reason, Uri target, int statusCode, int redirects) => new()
    {
        Success = false,
        Blocked = true,
        Error = reason,
        Url = target.ToString(),
        StatusCode = statusCode,
        RedirectCount = redirects,
    };

    private static WebFetchRefusedException? FindRefusal(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is WebFetchRefusedException refusal)
                return refusal;
        }

        return null;
    }

    private static string? TruncationNote(bool byteTruncated, bool charTruncated, int maxBytes, int maxChars)
    {
        if (byteTruncated && charTruncated)
            return $"the body exceeded both the {maxBytes}-byte and the {maxChars}-character ceilings";

        if (byteTruncated)
            return $"the body exceeded the {maxBytes}-byte ceiling; max_bytes accepts up to {MaxCeilingBytes}";

        if (charTruncated)
            return $"the extracted text exceeded the {maxChars}-character ceiling; max_chars accepts up to {MaxCeilingChars}";

        return null;
    }

    private static string? AppendNote(string? existing, string? addition)
    {
        if (string.IsNullOrWhiteSpace(addition))
            return existing;

        return string.IsNullOrWhiteSpace(existing) ? addition : $"{existing}; {addition}";
    }

    private static int Clamp(int value, int minimum, int maximum, int fallback) =>
        value <= 0 ? fallback : Math.Min(Math.Max(value, minimum), maximum);
}
