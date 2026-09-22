using System.Net;
using System.Text;
using System.Text.Json;
using TinadecTools.Abstractions;
using TinadecTools.Tools.Web;

namespace TinadecTools.Tests;

/// <summary>
/// <c>#fetch</c> is the transport Core uses to fill the extension market. It is a
/// reserved control call, so the two things that must both hold — invisible to the
/// model, and still bound by the same address policy as <c>web_fetch</c> — are the
/// assertions that matter most here. The rest is about it being *raw*: a catalog page
/// has to arrive as the document it is, not as a page summarised for a chat window.
/// </summary>
public sealed class RawFetchTests
{
    private static readonly IPAddress PublicAddress = IPAddress.Parse("93.184.216.34");

    private static ToolCallRequest<JsonElement> Call(string paramsJson, long callId = 7) => new()
    {
        ToolId = "#fetch",
        SessionId = "test",
        ToolCallId = callId,
        Approved = false,
        Params = JsonDocument.Parse(paramsJson).RootElement.Clone(),
    };

    private static async Task<RawFetchResult> FetchAsync(
        string paramsJson,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
        Func<HttpMessageHandler>? handlerFactory = null)
    {
        var response = await WebFetchTool.FetchRawAsync(Call(paramsJson), resolve, CancellationToken.None, handlerFactory);
        Assert.True(response.IsSuccess, "the call itself must succeed so Core gets a readable answer, not a bare error string");
        return JsonSerializer.Deserialize<RawFetchResult>(response.Response.GetRawText(), SnakeCase)
            ?? throw new InvalidOperationException("#fetch returned no body");
    }

    private static readonly JsonSerializerOptions SnakeCase = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Manifest_IsNeverOfferedToAModel()
    {
        GeneratedToolRegistry.RegisterAll();
        WebFetchTool.RegisterControl();

        var response = await ToolRegistry.DispatchAsync(new ToolCallRequest<JsonElement>
        {
            ToolId = "#manifest",
            SessionId = "test",
            ToolCallId = 1,
            Approved = false,
        });

        using var document = JsonDocument.Parse(response.Response.GetRawText());
        var ids = document.RootElement.GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("id").GetString())
            .ToList();

        Assert.Contains("web_fetch", ids);
        Assert.DoesNotContain("#fetch", ids);
    }

    [Fact]
    public async Task MissingUrl_IsAWireFailureRatherThanAnEmptyCatalog()
    {
        var response = await WebFetchTool.FetchRawAsync(Call("{}"), null, CancellationToken.None);

        Assert.False(response.IsSuccess);
        // An empty-but-successful answer is the shape that lets "we asked for
        // nothing" be stored as "this source has no entries".
        Assert.Contains("url", response.Response.GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MarkupArrivesUntouched()
    {
        var body = "<html><body><p>registry</p></body></html>";

        var result = await FetchAsync(
            """{"url":"https://registry.example/v0/servers"}""",
            handlerFactory: () => new CannedHandler(
                (request, _) => Reply(request, HttpStatusCode.OK, "text/html", body)));

        // This is the whole reason #fetch is not web_fetch: the same text/html body
        // that the model-facing tool flattens into "registry" has to arrive here as
        // the document, because a parser — not a reader — is on the other end.
        Assert.Equal(body, result.Body);
        Assert.True(result.Success);
        Assert.Equal("text/html", result.MediaType);
    }

    [Fact]
    public async Task ARedirectIsAnAnswerAndNotAStep()
    {
        var handler = new CannedHandler(
            (request, _) => Reply(request, HttpStatusCode.Found, "application/json", string.Empty, "https://elsewhere.example/v0/servers"));

        var result = await FetchAsync(
            """{"url":"https://registry.example/v0/servers"}""",
            handlerFactory: () => handler);

        Assert.False(result.Success);
        Assert.False(result.Blocked);
        Assert.Equal(302, result.StatusCode);
        Assert.Contains("elsewhere.example", result.Error, StringComparison.Ordinal);
        Assert.Null(result.Body);
        // Following it would hand Core bytes it never allowlisted.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task LoopbackStaysRefusedEvenOnAControlCall()
    {
        // No canned handler here: the real guarded transport runs, so this asserts
        // the policy the reserved id inherits rather than a stub of it.
        var result = await FetchAsync(
            """{"url":"http://127.0.0.1:48731/api/v1/health"}""",
            resolve: (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));

        Assert.True(result.Blocked, "a control call must not become the hole under the SSRF guard");
        Assert.False(result.Success);
        Assert.Null(result.Body);
    }

    [Fact]
    public async Task OverCeilingBodiesSayTheyWereCut()
    {
        var result = await FetchAsync(
            """{"url":"https://registry.example/v0/servers","max_bytes":8}""",
            handlerFactory: () => new CannedHandler(
                (request, _) => Reply(request, HttpStatusCode.OK, "application/json", "{\"servers\":[1,2,3]}")));

        Assert.True(result.Truncated);
        Assert.Equal(8, result.Body!.Length);
        Assert.Equal(8, result.ByteCount);
        // A cut JSON body is not a catalog: Core must be able to tell "there were
        // more bytes" from "this source is empty", which is why the flag exists.
        Assert.False(result.Success);
    }

    [Fact]
    public async Task BytesNobodyCanReadComeBackWithoutABody()
    {
        var result = await FetchAsync(
            """{"url":"https://registry.example/asset"}""",
            handlerFactory: () => new CannedHandler(
                (request, _) => Reply(request, HttpStatusCode.OK, "application/octet-stream", "binary")));

        Assert.False(result.Success);
        Assert.Null(result.Body);
        Assert.Equal(6, result.ByteCount);
        Assert.Contains("text only", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpstreamErrorsStillCarryTheDocument()
    {
        // A 4xx page is evidence for the refresh record ("the registry said this"),
        // the same reason web_fetch keeps it.
        var result = await FetchAsync(
            """{"url":"https://registry.example/v0/servers"}""",
            handlerFactory: () => new CannedHandler(
                (request, _) => Reply(request, HttpStatusCode.TooManyRequests, "application/json", "{\"error\":\"slow down\"}")));

        Assert.False(result.Success);
        Assert.Equal(429, result.StatusCode);
        Assert.Contains("slow down", result.Body, StringComparison.Ordinal);
    }

    private static Task<HttpResponseMessage> Reply(
        HttpRequestMessage request,
        HttpStatusCode status,
        string mediaType,
        string body,
        string? location = null)
    {
        var response = new HttpResponseMessage(status)
        {
            RequestMessage = request,
            Content = new StringContent(body, Encoding.UTF8, mediaType),
        };
        if (location is not null)
            response.Headers.Location = new Uri(location);
        return Task.FromResult(response);
    }

    private sealed class CannedHandler(params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] replies) : HttpMessageHandler
    {
        private int _index;

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var reply = replies[Math.Min(_index++, replies.Length - 1)];
            return reply(request, cancellationToken);
        }
    }
}
