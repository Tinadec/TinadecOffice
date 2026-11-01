using System.Net;
using System.Text;
using System.Text.Json;
using TinadecTools.Tools.Browser;

namespace TinadecTools.Tests;

public sealed class BrowserBridgeClientTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return _handler(request, cancellationToken);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static (string Home, string File) CreateHome(string? bridgeJson = null)
    {
        var home = Path.Combine(Path.GetTempPath(), "tinadec-bridge-client-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(home, "state", "agent-browser");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "bridge.json");
        if (bridgeJson is not null) File.WriteAllText(file, bridgeJson);
        return (home, file);
    }

    private static string BridgeJson(string url = "http://127.0.0.1:12345", string token = "secret") =>
        $$"""{"version":1,"url":"{{url}}","token":"{{token}}"}""";

    [Fact]
    public async Task MissingBridgeFile_ReportsHostUnavailable()
    {
        var (home, _) = CreateHome();
        var client = new BrowserBridgeClient(new FakeHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{}"))), () => home, null);

        var response = await client.SendAsync(new BrowserBridgeCall("state", "s1"), TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.False(response.Ok);
        Assert.Equal("browser_host_unavailable", response.Code);
    }

    [Fact]
    public async Task InvalidBridgeJson_ReportsHostUnavailable()
    {
        var (home, _) = CreateHome("{ not json");
        var client = new BrowserBridgeClient(new FakeHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{}"))), () => home, null);

        var response = await client.SendAsync(new BrowserBridgeCall("state", "s1"), TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Equal("browser_host_unavailable", response.Code);
    }

    [Fact]
    public async Task NonLoopbackBridgeUrl_IsRefused()
    {
        var (home, _) = CreateHome(BridgeJson("http://example.com:12345"));
        var client = new BrowserBridgeClient(new FakeHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{}"))), () => home, null);

        var response = await client.SendAsync(new BrowserBridgeCall("state", "s1"), TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Equal("browser_host_unavailable", response.Code);
    }

    [Fact]
    public async Task SendsTheBearerTokenAndParsesData()
    {
        var (home, _) = CreateHome(BridgeJson());
        string? authorization = null;
        string? body = null;
        var handler = new FakeHandler(async (request, cancellationToken) =>
        {
            authorization = request.Headers.Authorization?.ToString();
            body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return Json(HttpStatusCode.OK, """{"ok":true,"data":{"control":"agent"}}""");
        });
        var client = new BrowserBridgeClient(handler, () => home, null);

        var response = await client.SendAsync(new BrowserBridgeCall("state", "s1"), TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.True(response.Ok);
        Assert.Equal("Bearer secret", authorization);
        Assert.Equal("agent", response.Data!.Value.GetProperty("control").GetString());
        using var document = JsonDocument.Parse(body!);
        Assert.Equal("state", document.RootElement.GetProperty("op").GetString());
        Assert.Equal("s1", document.RootElement.GetProperty("session_id").GetString());
    }

    [Fact]
    public async Task IncludesCdpParameters()
    {
        var (home, _) = CreateHome(BridgeJson());
        string? body = null;
        var handler = new FakeHandler(async (request, cancellationToken) =>
        {
            body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Json(HttpStatusCode.OK, """{"ok":true,"data":{}}""");
        });
        var client = new BrowserBridgeClient(handler, () => home, null);
        using var parameters = JsonDocument.Parse("""{"url":"https://example.com/"}""");

        await client.SendAsync(
            new BrowserBridgeCall("cdp", "s1", "tab-1", "Page.navigate", parameters.RootElement.Clone()),
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        using var document = JsonDocument.Parse(body!);
        Assert.Equal("cdp", document.RootElement.GetProperty("op").GetString());
        Assert.Equal("tab-1", document.RootElement.GetProperty("tab_id").GetString());
        Assert.Equal("Page.navigate", document.RootElement.GetProperty("method").GetString());
        Assert.Equal("https://example.com/", document.RootElement.GetProperty("params").GetProperty("url").GetString());
    }

    [Fact]
    public async Task BusinessFailureKeepsItsStableCode()
    {
        var (home, _) = CreateHome(BridgeJson());
        var client = new BrowserBridgeClient(new FakeHandler((_, _) => Task.FromResult(Json(
            HttpStatusCode.OK,
            """{"ok":false,"code":"browser_user_in_control","permanent":true,"message":"taken" }"""))), () => home, null);

        var response = await client.SendAsync(new BrowserBridgeCall("cdp", "s1", "t1", "Page.reload"), TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.False(response.Ok);
        Assert.Equal("browser_user_in_control", response.Code);
        Assert.True(response.Permanent);
        Assert.Equal("taken", response.Message);
    }

    [Fact]
    public async Task A401RereadsTheEndpointOnceAndRetries()
    {
        var (home, _) = CreateHome(BridgeJson());
        var calls = 0;
        var handler = new FakeHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? Json(HttpStatusCode.Unauthorized, """{"ok":false,"code":"browser_unauthorized"}""")
                : Json(HttpStatusCode.OK, """{"ok":true,"data":{}}"""));
        });
        var client = new BrowserBridgeClient(handler, () => home, null);

        var response = await client.SendAsync(new BrowserBridgeCall("state", "s1"), TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.True(response.Ok);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Persistent401_ReportsUnauthorized()
    {
        var (home, _) = CreateHome(BridgeJson());
        var handler = new FakeHandler((_, _) => Task.FromResult(Json(HttpStatusCode.Unauthorized, """{"ok":false,"code":"browser_unauthorized"}""")));
        var client = new BrowserBridgeClient(handler, () => home, null);

        var response = await client.SendAsync(new BrowserBridgeCall("state", "s1"), TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Equal("browser_unauthorized", response.Code);
    }

    [Fact]
    public async Task A500_IsATransientBridgeError()
    {
        var (home, _) = CreateHome(BridgeJson());
        var client = new BrowserBridgeClient(new FakeHandler((_, _) => Task.FromResult(Json(HttpStatusCode.InternalServerError, "{}"))), () => home, null);

        var response = await client.SendAsync(new BrowserBridgeCall("state", "s1"), TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Equal("browser_bridge_error", response.Code);
        Assert.False(response.Permanent);
    }

    [Fact]
    public async Task ATimeout_IsReportedAsTransient()
    {
        var (home, _) = CreateHome(BridgeJson());
        var client = new BrowserBridgeClient(new FakeHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Json(HttpStatusCode.OK, "{}");
        }), () => home, null);

        var response = await client.SendAsync(new BrowserBridgeCall("state", "s1"), TimeSpan.FromMilliseconds(80), CancellationToken.None);

        Assert.Equal("browser_timeout", response.Code);
        Assert.False(response.Permanent);
    }
}
