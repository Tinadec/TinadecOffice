using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TinadecTools.Abstractions;
using TinadecTools.Tools.Web;

namespace TinadecTools.Tests;

/// <summary>
/// <c>web_fetch</c> is the first tool that lets a run reach outside the machine, so
/// the interesting assertions are all about what it must NOT do: fetch a loopback or
/// metadata address, forward URL credentials, return a binary body, or report a
/// cut-off page as if it were the whole page.
/// </summary>
public sealed class WebFetchTests
{
    private static readonly IPAddress PublicAddress = IPAddress.Parse("93.184.216.34");

    [Fact]
    public async Task ManifestEntry_IsApprovalGatedConfirmableAndNotAWorkspaceMutation()
    {
        GeneratedToolRegistry.RegisterAll();

        var response = await ToolRegistry.DispatchAsync(new ToolCallRequest<JsonElement>
        {
            ToolId = "#manifest",
            SessionId = "test",
            ToolCallId = 1,
            Approved = false,
        });

        using var document = JsonDocument.Parse(response.Response.GetRawText());
        var entry = document.RootElement.GetProperty("tools").EnumerateArray()
            .Single(tool => tool.GetProperty("id").GetString() == "web_fetch");

        Assert.True(entry.GetProperty("requires_approval").GetBoolean());
        Assert.Equal("high", entry.GetProperty("risk").GetString());

        // RequiresApproval alone marks a tool as mutating the workspace, which
        // would put reading a web page behind the write-path policy.
        Assert.False(entry.GetProperty("mutates_workspace").GetBoolean());
        Assert.Equal("safe", entry.GetProperty("retry_safety").GetString());
        Assert.Equal("confirm_fetch", entry.GetProperty("confirmation_fields").EnumerateArray().Select(value => value.GetString()).Single());

        var properties = entry.GetProperty("input_schema").GetProperty("properties");
        foreach (var name in new[] { "url", "max_bytes", "max_chars", "timeout_ms", "confirm_fetch" })
        {
            Assert.True(properties.TryGetProperty(name, out _), $"input_schema is missing '{name}'");
        }
    }

    [Fact]
    public async Task FetchAsync_WithoutConfirmation_DoesNotReachTheNetwork()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WebFetchTool.FetchAsync(new WebFetchArgs { Url = "http://example.com/" }, CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData("", "required")]
    [InlineData("example.com/path", "http")]
    [InlineData("ftp://example.com/pub", "ftp")]
    [InlineData("file:///etc/passwd", "file")]
    [InlineData("https://user:secret@example.com/", "credential")]
    public async Task FetchAsync_RejectsUnusableUrlsBeforeAnyResolution(string url, string expectedWord)
    {
        var result = await WebFetchTool.FetchAsync(
            new WebFetchArgs { Url = url, ConfirmFetch = "yes" },
            (_, _) => Task.FromException<IPAddress[]>(new InvalidOperationException("the resolver must not run")),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.Blocked);
        Assert.Contains(expectedWord, result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryBuildTarget_DropsTheFragmentAndKeepsTheQuery()
    {
        Assert.True(WebFetchGuard.TryBuildTarget("https://example.com/docs?a=1#section", out var target, out _));
        Assert.Equal("?a=1", target!.Query);
        Assert.DoesNotContain("#section", target.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.5.5.5")]
    [InlineData("0.0.0.0")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.9")]
    [InlineData("192.168.4.4")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.5")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("fec0::1")]
    [InlineData("2001::4136:e378:8006:21ff:3cd:f9")]
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("::ffff:127.0.0.1")]
    public void BlockReason_RefusesInternalAndTunnelShapes(string literal) =>
        Assert.NotNull(WebFetchGuard.BlockReason(IPAddress.Parse(literal)));

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("8.8.4.4")]
    public void BlockReason_AllowsPublicUnicast(string literal) =>
        Assert.Null(WebFetchGuard.BlockReason(IPAddress.Parse(literal)));

    [Fact]
    public async Task ResolveAsync_RefusesWhenEveryAnswerIsInternal()
    {
        var refusal = await Assert.ThrowsAsync<WebFetchRefusedException>(() => WebFetchGuard.ResolveAllowedAsync(
            "internal.test",
            (_, _) => Task.FromResult(new[] { IPAddress.Loopback, IPAddress.Parse("10.0.0.8") }),
            CancellationToken.None));

        Assert.Contains("refuses", refusal.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("internal.test", refusal.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_KeepsAPublicAnswerAndDropsTheInternalOne()
    {
        // The rebinding shape: one name, two answers, only one of them fetchable.
        var allowed = await WebFetchGuard.ResolveAllowedAsync(
            "mixed.test",
            (_, _) => Task.FromResult(new[] { IPAddress.Loopback, PublicAddress }),
            CancellationToken.None);

        var address = Assert.Single(allowed);
        Assert.Equal(PublicAddress, address);
    }

    [Fact]
    public async Task ResolveAsync_StripsIPv6BracketsBeforeAsking()
    {
        var asked = string.Empty;
        await WebFetchGuard.ResolveAllowedAsync(
            "[2606:4700:4700::1111]",
            (host, _) =>
            {
                asked = host;
                return Task.FromResult(new[] { IPAddress.Parse("2606:4700:4700::1111") });
            },
            CancellationToken.None);

        Assert.Equal("2606:4700:4700::1111", asked);
    }

    [Fact]
    public async Task ConnectPath_NeverOpensASocketToABlockedAddress()
    {
        // No transport seam here: this is the production handler, and the connect
        // callback has to refuse before a socket exists. A loopback answer would
        // otherwise be reachable from this machine in milliseconds.
        var result = await WebFetchTool.FetchAsync(
            new WebFetchArgs { Url = "http://metadata.test/latest", ConfirmFetch = "yes", TimeoutMs = 3_000 },
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("169.254.169.254") }),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.Blocked);
        Assert.Contains("link-local", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Text);
    }

    [Fact]
    public async Task FetchAsync_ReturnsReadableTextForHtml()
    {
        const string html = """
            <html><head><title>Prices</title><style>body{color:red}</style></head>
            <body><script>var secret = "do not read me";</script>
            <!-- a comment nobody asked for -->
            <h1>July</h1><p>Steel is &amp; cheap &mdash; 5&cent; per kg.</p>
            <p>Second paragraph.</p></body></html>
            """;

        var result = await FetchAsync(new StringContent(html, Encoding.UTF8, "text/html"));

        Assert.True(result.Success);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("text/html", result.MediaType);
        Assert.False(result.Truncated);
        Assert.Contains("Steel is & cheap — 5", result.Text, StringComparison.Ordinal);
        Assert.Contains("Second paragraph", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("do not read me", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("color:red", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("nobody asked for", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("<", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchAsync_ReportsBothCeilingsAsTruncation()
    {
        var body = new string('x', 5_000);

        var bytewise = await FetchAsync(new StringContent(body, Encoding.UTF8, "text/plain"), mediaType: "text/plain", maxBytes: 128);
        Assert.True(bytewise.Success);
        Assert.True(bytewise.Truncated);
        Assert.Equal(128, bytewise.ByteCount);
        Assert.Contains("128-byte ceiling", bytewise.Note, StringComparison.Ordinal);

        var charwise = await FetchAsync(new StringContent(body, Encoding.UTF8, "text/plain"), mediaType: "text/plain", maxChars: 40);
        Assert.True(charwise.Truncated);
        Assert.Equal(40, charwise.Text!.Length);
        Assert.Contains("40-character ceiling", charwise.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchAsync_KeepsTheBodyOfAnErrorStatusAndStillFails()
    {
        var result = await FetchAsync(
            new StringContent("<h1>No such page</h1>", Encoding.UTF8, "text/html"),
            status: HttpStatusCode.NotFound);

        Assert.False(result.Success);
        Assert.Equal(404, result.StatusCode);
        Assert.Contains("HTTP 404", result.Error, StringComparison.Ordinal);
        Assert.Contains("No such page", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchAsync_DoesNotReturnBinaryBodies()
    {
        var png = new byte[4096];
        png[0] = 0x89;

        var result = await FetchAsync(new ByteArrayContent(png), mediaType: "image/png");

        Assert.False(result.Success);
        Assert.Null(result.Text);
        Assert.Equal("image/png", result.MediaType);
        Assert.Contains("image/png", result.Error, StringComparison.Ordinal);
        Assert.Contains("4096 byte", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchAsync_RevalidatesEveryRedirectHop()
    {
        var handler = new CannedHandler(
            (_, _) => Task.FromResult(Redirect("http://elsewhere.test/ok")),
            (_, _) => Task.FromResult(Redirect("file:///etc/passwd")));

        var result = await WebFetchTool.FetchAsync(
            new WebFetchArgs { Url = "http://start.test/", ConfirmFetch = "yes" },
            (_, _) => Task.FromResult(new[] { PublicAddress }),
            CancellationToken.None,
            () => handler);

        Assert.False(result.Success);
        Assert.True(result.Blocked);
        Assert.Contains("file", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.Calls);

        // The refusal names the address we stopped at, not the one we were sent to:
        // "http://start.test/" was fine, the hop is the problem.
        Assert.Contains("elsewhere.test", result.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchAsync_StopsAfterTooManyRedirects()
    {
        var hops = Enumerable.Range(1, WebFetchGuard.MaxRedirections + 2)
            .Select(index => ((Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>) ((_, _) => Task.FromResult(Redirect($"http://hop{index}.test/")))))
            .ToArray();

        var result = await WebFetchTool.FetchAsync(
            new WebFetchArgs { Url = "http://start.test/", ConfirmFetch = "yes" },
            (_, _) => Task.FromResult(new[] { PublicAddress }),
            CancellationToken.None,
            () => new CannedHandler(hops));

        Assert.True(result.Blocked);
        Assert.Equal(WebFetchGuard.MaxRedirections, result.RedirectCount);
        Assert.Contains("more than 3 times", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchAsync_TurnsItsOwnTimeoutIntoAnAnswer()
    {
        var handler = new CannedHandler(async (_, token) =>
        {
            await Task.Delay(5_000, token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("late") };
        });

        var result = await WebFetchTool.FetchAsync(
            new WebFetchArgs { Url = "http://slow.test/", ConfirmFetch = "yes", TimeoutMs = 1_000 },
            (_, _) => Task.FromResult(new[] { PublicAddress }),
            CancellationToken.None,
            () => handler);

        Assert.False(result.Success);
        Assert.False(result.Blocked);
        Assert.Contains("1000 ms", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchAsync_NotesAnUnsupportedCharsetInsteadOfFailing()
    {
        var result = await FetchAsync(
            new StringContent("plain text", Encoding.UTF8, "text/plain"),
            mediaType: "text/plain",
            charset: "big5");

        Assert.True(result.Success);
        Assert.Contains("big5", result.Note, StringComparison.Ordinal);
        Assert.Equal("plain text", result.Text);
    }

    [Fact]
    public void CapChars_CutsBeforeALoneSurrogate()
    {
        var capped = WebFetchContent.CapChars("ab😀cd", 3, out var truncated);

        Assert.True(truncated);
        Assert.Equal("ab", capped);
    }

    [Theory]
    [InlineData("text/html", true)]
    [InlineData("text/plain", true)]
    [InlineData("application/json", true)]
    [InlineData("application/vnd.api+json", true)]
    [InlineData("application/xml", true)]
    [InlineData("application/pdf", false)]
    [InlineData("image/png", false)]
    [InlineData("application/octet-stream", false)]
    [InlineData("", false)]
    public void IsTextual_OnlyAdmitsMediaThatCanBeRead(string mediaType, bool expected) =>
        Assert.Equal(expected, WebFetchContent.IsTextual(mediaType));

    [Fact]
    public void MediaTypeOf_StripsParameters() =>
        Assert.Equal("text/html", WebFetchContent.MediaTypeOf("Text/HTML; charset=utf-8"));

    private static ValueTask<WebFetchResult> FetchAsync(
        HttpContent content,
        HttpStatusCode status = HttpStatusCode.OK,
        string mediaType = "text/html",
        string? charset = null,
        int maxBytes = 0,
        int maxChars = 0)
    {
        var header = new MediaTypeHeaderValue(mediaType);
        if (charset is not null)
            header.Parameters.Add(new NameValueHeaderValue("charset", charset));

        content.Headers.ContentType = header;

        var handler = new CannedHandler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = content }));

        return WebFetchTool.FetchAsync(
            new WebFetchArgs { Url = "http://public.test/page", ConfirmFetch = "yes", MaxBytes = maxBytes, MaxChars = maxChars },
            (_, _) => Task.FromResult(new[] { PublicAddress }),
            CancellationToken.None,
            () => handler);
    }

    private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Found)
    {
        Headers = { Location = new Uri(location) },
    };

    private sealed class CannedHandler(params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] replies) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] _replies = replies;

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var reply = _replies[Math.Min(Calls, _replies.Length - 1)];
            Calls++;
            return reply(request, cancellationToken);
        }
    }
}
