using System.Text.Json;
using TinadecTools.Tools.Browser;

namespace TinadecTools.Tests;

public sealed class BrowserToolRuntimeTests
{
    private sealed class FakeBridge : IBrowserBridge
    {
        public List<BrowserBridgeCall> Calls { get; } = new();
        public Func<BrowserBridgeCall, BrowserBridgeResponse> Handler { get; set; } =
            _ => BrowserBridgeResponse.Failure("browser_bridge_error", "not configured");

        public ValueTask<BrowserBridgeResponse> SendAsync(
            BrowserBridgeCall call,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Calls.Add(call);
            return new ValueTask<BrowserBridgeResponse>(Handler(call));
        }
    }

    private const string AxTree = """
    {"nodes":[
      {"nodeId":"1","role":{"value":"RootWebArea"},"name":{"value":"Probe"},"childIds":["2"]},
      {"nodeId":"2","role":{"value":"button"},"name":{"value":"Click me"},"backendDOMNodeId":6,"childIds":[]}
    ]}
    """;

    private const string StateFrame = """{"frameTree":{"frame":{"id":"f1","loaderId":"L1"}}}""";

    private static BrowserBridgeResponse Ok(string data)
    {
        using var document = JsonDocument.Parse(data);
        return new BrowserBridgeResponse { Ok = true, Data = document.RootElement.Clone() };
    }

    private static BrowserBridgeResponse Fail(string code) => BrowserBridgeResponse.Failure(code, code, true);

    private static string StateJson(
        string control = "agent",
        bool withTab = true,
        string? activeTab = "t1",
        string dialog = "null",
        string notices = "[]",
        long noticeSeq = 0)
    {
        const string tab = """[{"id":"t1","url":"https://example.com/","title":"Example","active":true}]""";
        var tabs = withTab ? tab : "[]";
        var active = activeTab is null ? "null" : $"\"{activeTab}\"";
        return $$"""{"control":"{{control}}","tabs":{{tabs}},"active_tab":{{active}},"dialog":{{dialog}},"notices":{{notices}},"notice_seq":{{noticeSeq}}}""";
    }

    private static string CdpData(string resultJson, string notices = "[]", long noticeSeq = 0) =>
        $$"""{"result":{{resultJson}},"notices":{{notices}},"notice_seq":{{noticeSeq}}}""";

    private static string FrameResult(string loaderId) =>
        "{\"frameTree\":{\"frame\":{\"id\":\"f1\",\"loaderId\":\"" + loaderId + "\"}}}";

    private static string AxResult(int backendId) =>
        "{\"nodes\":[{\"nodeId\":\"1\",\"role\":{\"value\":\"RootWebArea\"},\"name\":{\"value\":\"Probe\"},\"childIds\":[\"2\"]}," +
        "{\"nodeId\":\"2\",\"role\":{\"value\":\"button\"},\"name\":{\"value\":\"Button " + backendId + "\"}," +
        "\"backendDOMNodeId\":" + backendId + ",\"childIds\":[]}]}";

    private static BrowserToolRuntime Runtime(FakeBridge bridge) =>
        new(bridge, new BrowserNavigationPolicy(allowPrivate: true));

    [Fact]
    public async Task Snapshot_ReturnsCompactTreeAndRefs()
    {
        var bridge = new FakeBridge();
        bridge.Handler = call => call switch
        {
            { Op: "state" } => Ok(StateJson()),
            { Op: "cdp", Method: "Page.getFrameTree" } => Ok(CdpData(StateFrame)),
            { Op: "cdp", Method: "Accessibility.getFullAXTree" } => Ok(CdpData(AxTree)),
            _ => Fail("browser_bridge_error"),
        };

        var result = await Runtime(bridge).SnapshotAsync("s1", new BrowserSnapshotArgs(), CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("t1", result.TabId);
        Assert.Equal(1, result.RefCount);
        Assert.Contains("button \"Click me\" [ref=1]", result.Snapshot);
        Assert.Equal("https://example.com/", result.Url);
    }

    [Fact]
    public async Task Snapshot_KeepsRefsAcrossSnapshotsAndResetsOnANewDocument()
    {
        var loader = "L1";
        var buttonBackend = 6;
        var bridge = new FakeBridge();
        bridge.Handler = call => call switch
        {
            { Op: "state" } => Ok(StateJson()),
            { Op: "cdp", Method: "Page.getFrameTree" } => Ok(CdpData(FrameResult(loader))),
            { Op: "cdp", Method: "Accessibility.getFullAXTree" } => Ok(CdpData(AxResult(buttonBackend))),
            _ => Fail("browser_bridge_error"),
        };
        var runtime = Runtime(bridge);

        var first = await runtime.SnapshotAsync("s1", new BrowserSnapshotArgs(), CancellationToken.None);
        Assert.Contains("[ref=1]", first.Snapshot);

        var repeat = await runtime.SnapshotAsync("s1", new BrowserSnapshotArgs(), CancellationToken.None);
        Assert.Contains("[ref=1]", repeat.Snapshot);
        Assert.Equal(1, repeat.RefCount);

        buttonBackend = 9;
        var second = await runtime.SnapshotAsync("s1", new BrowserSnapshotArgs(), CancellationToken.None);
        Assert.Contains("[ref=2]", second.Snapshot);
        Assert.Equal(1, second.RefCount);

        loader = "L2";
        buttonBackend = 10;
        var third = await runtime.SnapshotAsync("s1", new BrowserSnapshotArgs(), CancellationToken.None);
        Assert.Contains("Button 10", third.Snapshot);
        Assert.Contains("[ref=1]", third.Snapshot);
        Assert.Equal(1, third.RefCount);
    }

    [Fact]
    public async Task Snapshot_HardStopsWhenTheUserHasControlOrADialogIsOpen()
    {
        var bridge = new FakeBridge();
        bridge.Handler = call => call switch
        {
            { Op: "state" } => Ok(StateJson(control: "user")),
            _ => Fail("browser_bridge_error"),
        };
        var controlled = await Runtime(bridge).SnapshotAsync("s1", new BrowserSnapshotArgs(), CancellationToken.None);
        Assert.False(controlled.Ok);
        Assert.Equal("browser_user_in_control", controlled.Code);
        Assert.True(controlled.Permanent);

        bridge.Handler = call => call switch
        {
            { Op: "state" } => Ok(StateJson(dialog: """{"type":"alert","message":"hi"}""")),
            _ => Fail("browser_bridge_error"),
        };
        var dialog = await Runtime(bridge).SnapshotAsync("s2", new BrowserSnapshotArgs(), CancellationToken.None);
        Assert.Equal("browser_dialog_open", dialog.Code);
        Assert.Contains("hi", dialog.Message);
    }

    [Fact]
    public async Task Snapshot_ReportsHardStopNoticesOnceThenLetsTheNextCallProceed()
    {
        var bridge = new FakeBridge();
        bridge.Handler = call => call switch
        {
            { Op: "state" } => Ok(StateJson(
                notices: """[{"seq":1,"code":"browser_permission_denied","permission":"geolocation"}]""",
                noticeSeq: 1)),
            { Op: "cdp", Method: "Page.getFrameTree" } => Ok(CdpData(StateFrame)),
            { Op: "cdp", Method: "Accessibility.getFullAXTree" } => Ok(CdpData(AxTree)),
            _ => Fail("browser_bridge_error"),
        };
        var runtime = Runtime(bridge);

        var first = await runtime.SnapshotAsync("s1", new BrowserSnapshotArgs(), CancellationToken.None);
        Assert.False(first.Ok);
        Assert.Equal("browser_permission_denied", first.Code);
        Assert.Single(first.Notices!);

        var second = await runtime.SnapshotAsync("s1", new BrowserSnapshotArgs(), CancellationToken.None);
        Assert.True(second.Ok, second.Message);
        Assert.Null(second.Notices);
    }

    [Fact]
    public async Task Snapshot_ReturnsBrowserNotStartedWhenThereIsNoTab()
    {
        var bridge = new FakeBridge();
        bridge.Handler = call => call switch
        {
            { Op: "state" } => Ok(StateJson(withTab: false, activeTab: null)),
            _ => Fail("browser_bridge_error"),
        };

        var result = await Runtime(bridge).SnapshotAsync("s1", new BrowserSnapshotArgs(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("browser_not_started", result.Code);
    }

    [Fact]
    public async Task Snapshot_PropagatesABridgeFailure()
    {
        var bridge = new FakeBridge { Handler = _ => Fail("browser_host_unavailable") };
        var result = await Runtime(bridge).SnapshotAsync("s1", new BrowserSnapshotArgs(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("browser_host_unavailable", result.Code);
    }

    [Fact]
    public async Task Navigate_OpensTheBrowserOnFirstUseAndWaitsForReady()
    {
        var stateCalls = 0;
        var bridge = new FakeBridge();
        bridge.Handler = call => call switch
        {
            { Op: "state" } => Ok(StateJson(withTab: ++stateCalls > 1)),
            { Op: "tabs/open" } => Ok("""{"tab":{"id":"t1","url":"https://example.com/","title":"Example","active":true},"state":{},"notices":[],"notice_seq":0}"""),
            { Op: "cdp", Method: "Runtime.evaluate" } => Ok(CdpData("""{"result":{"objectId":"window-1"}}""")),
            { Op: "cdp", Method: "Runtime.callFunctionOn" } => Ok(CdpData("""{"result":{"type":"boolean","value":true}}""")),
            _ => Fail("browser_bridge_error"),
        };

        var result = await Runtime(bridge).NavigateAsync(
            "s1", new BrowserNavigateArgs { Url = "https://example.com/", WaitMs = 2000 }, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.True(result.Ready);
        Assert.Equal("https://example.com/", result.Url);
        var open = bridge.Calls.Single(call => call.Op == "tabs/open");
        Assert.Equal("https://example.com/", open.Url);
    }

    [Fact]
    public async Task Navigate_RefusesAJavascriptUrlBeforeTouchingTheBridge()
    {
        var bridge = new FakeBridge();
        var result = await Runtime(bridge).NavigateAsync(
            "s1", new BrowserNavigateArgs { Url = "javascript:alert(1)" }, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("browser_navigation_blocked", result.Code);
        Assert.Empty(bridge.Calls);
    }

    [Fact]
    public async Task Wait_ReportsTextAndTimeout()
    {
        var bridge = new FakeBridge();
        bridge.Handler = call => call switch
        {
            { Op: "state" } => Ok(StateJson()),
            { Op: "cdp", Method: "Runtime.evaluate" } => Ok(CdpData("""{"result":{"objectId":"window-1"}}""")),
            { Op: "cdp", Method: "Runtime.callFunctionOn" } => Ok(CdpData("""{"result":{"value":{"ok":true,"reason":"text"}}}""")),
            _ => Fail("browser_bridge_error"),
        };
        var runtime = Runtime(bridge);

        var found = await runtime.WaitAsync("s1", new BrowserWaitArgs { Text = "hello" }, CancellationToken.None);
        Assert.True(found.Ok);
        Assert.Equal("text", found.Reason);

        bridge.Handler = call => call switch
        {
            { Op: "state" } => Ok(StateJson()),
            { Op: "cdp", Method: "Runtime.evaluate" } => Ok(CdpData("""{"result":{"objectId":"window-1"}}""")),
            { Op: "cdp", Method: "Runtime.callFunctionOn" } => Ok(CdpData("""{"result":{"value":{"ok":false,"reason":"timeout"}}}""")),
            _ => Fail("browser_bridge_error"),
        };
        var timeout = await runtime.WaitAsync("s2", new BrowserWaitArgs { Text = "missing" }, CancellationToken.None);
        Assert.False(timeout.Ok);
        Assert.Equal("browser_wait_timeout", timeout.Code);
        Assert.False(timeout.Permanent);
    }

    [Fact]
    public async Task Wait_RequiresACondition()
    {
        var result = await Runtime(new FakeBridge()).WaitAsync("s1", new BrowserWaitArgs(), CancellationToken.None);
        Assert.Equal("browser_wait_invalid", result.Code);
    }

    [Fact]
    public async Task TabsList_ReturnsTheBridgeTabs()
    {
        var bridge = new FakeBridge();
        bridge.Handler = call => call switch
        {
            { Op: "tabs/list" } => Ok("""[{"id":"t1","url":"https://a/","title":"A","active":true},{"id":"t2","url":"https://b/","title":"B","active":false}]"""),
            _ => Fail("browser_bridge_error"),
        };

        var result = await Runtime(bridge).TabsListAsync("s1", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Tabs!.Length);
        Assert.Equal("t1", result.Tabs[0].Id);
        Assert.True(result.Tabs[0].Active);
        Assert.False(result.Tabs[1].Active);
    }

    [Fact]
    public async Task Screenshot_ReturnsThePathAndBytes()
    {
        var bridge = new FakeBridge();
        bridge.Handler = call => call switch
        {
            { Op: "state" } => Ok(StateJson()),
            { Op: "screenshot" } => Ok("""{"path":"C:/tmp/shot.png","bytes":1234,"notices":[],"notice_seq":0}"""),
            _ => Fail("browser_bridge_error"),
        };

        var result = await Runtime(bridge).ScreenshotAsync("s1", new BrowserScreenshotArgs { FullPage = true }, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("C:/tmp/shot.png", result.Path);
        Assert.Equal(1234, result.Bytes);
        Assert.True(bridge.Calls.Single(call => call.Op == "screenshot").FullPage);
    }
}
