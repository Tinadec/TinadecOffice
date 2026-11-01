using System.Text.Json;
using TinadecTools.Tools.Browser;

namespace TinadecTools.Tests;

public sealed class BrowserActionTests
{
    private sealed class Harness
    {
        public FakeBridge Bridge { get; } = new();
        public string Loader { get; set; } = "L1";
        public bool HitOk { get; set; } = true;
        public string HitReason { get; set; } = "obscured";
        public bool FallbackFirstRead { get; set; }
        public int ReadCalls { get; private set; }
        public BrowserToolRuntime Runtime { get; }

        public Harness()
        {
            Bridge.Handler = Handle;
            Runtime = new BrowserToolRuntime(Bridge, new BrowserNavigationPolicy(allowPrivate: true));
        }

        public Task<BrowserToolResult> SnapshotAsync() =>
            Runtime.SnapshotAsync("s1", new BrowserSnapshotArgs(), CancellationToken.None);

        public List<BrowserBridgeCall> InputCalls(string method) =>
            Bridge.Calls.Where(call => call.Op == "cdp" && call.Method == method).ToList();

        private BrowserBridgeResponse Handle(BrowserBridgeCall call)
        {
            if (call.Op == "state") return Ok(StateJson());
            if (call.Op == "tabs/open")
            {
                return Ok("""{"tab":{"id":"t1","url":"https://example.com/","title":"Example","active":true},"state":{},"notices":[],"notice_seq":0}""");
            }
            if (call.Op is "tabs/select" or "tabs/close")
            {
                return Ok("""{"state":{},"notices":[],"notice_seq":0}""");
            }
            if (call.Op != "cdp") return Fail("browser_bridge_error");

            switch (call.Method)
            {
                case "Page.getFrameTree":
                    return Ok(CdpData("{\"frameTree\":{\"frame\":{\"id\":\"f1\",\"loaderId\":\"" + Loader + "\"}}}"));
                case "Accessibility.getFullAXTree":
                    return Ok(CdpData(AxTree));
                case "DOM.scrollIntoViewIfNeeded":
                case "DOM.focus":
                    return Ok(CdpData("{}"));
                case "DOM.getBoxModel":
                    return Ok(CdpData("""{"model":{"content":[10,10,30,10,30,30,10,30]}}"""));
                case "DOM.resolveNode":
                    return Ok(CdpData("""{"object":{"objectId":"obj-1"}}"""));
                case "Runtime.evaluate":
                    return Ok(CdpData("""{"result":{"value":{"w":800,"h":600}}}"""));
                case "Runtime.callFunctionOn":
                    return HandleCallFunction(call);
                case "Input.dispatchMouseEvent":
                case "Input.dispatchKeyEvent":
                case "Input.insertText":
                    return Ok(CdpData("{}"));
                case "Page.getNavigationHistory":
                    return Ok(CdpData("""{"currentIndex":1,"entries":[{"id":1},{"id":2}]}"""));
                case "Page.navigateToHistoryEntry":
                case "Page.reload":
                    return Ok(CdpData("{}"));
                default:
                    return Fail("browser_bridge_error");
            }
        }

        private BrowserBridgeResponse HandleCallFunction(BrowserBridgeCall call)
        {
            var function = call.Parameters is { ValueKind: JsonValueKind.Object } parameters
                           && parameters.TryGetProperty("functionDeclaration", out var declaration)
                ? declaration.GetString() ?? string.Empty
                : string.Empty;

            if (function.Contains("elementFromPoint", StringComparison.Ordinal))
            {
                return Ok(CdpData(HitOk
                    ? """{"result":{"value":{"ok":true}}}"""
                    : "{\"result\":{\"value\":{\"ok\":false,\"reason\":\"" + HitReason + "\"}}}"));
            }
            if (function.Contains("setSelectionRange", StringComparison.Ordinal))
            {
                return Ok(CdpData("""{"result":{"value":true}}"""));
            }
            if (function.Contains("HTMLInputElement", StringComparison.Ordinal))
            {
                return Ok(CdpData("""{"result":{"value":"hello"}}"""));
            }
            if (function.Contains("this.value!==undefined", StringComparison.Ordinal))
            {
                ReadCalls++;
                var value = FallbackFirstRead && ReadCalls == 1 ? "old" : "hello";
                return Ok(CdpData("{\"result\":{\"value\":\"" + value + "\"}}"));
            }
            return Fail("browser_bridge_error");
        }

        private const string AxTree = """
        {"nodes":[
          {"nodeId":"1","role":{"value":"RootWebArea"},"name":{"value":"Probe"},"childIds":["2"]},
          {"nodeId":"2","role":{"value":"button"},"name":{"value":"Click me"},"backendDOMNodeId":6,"childIds":[]}
        ]}
        """;

        private static string StateJson() =>
            """{"control":"agent","tabs":[{"id":"t1","url":"https://example.com/","title":"Example","active":true}],"active_tab":"t1","dialog":null,"notices":[],"notice_seq":0}""";

        private static string CdpData(string resultJson) =>
            "{\"result\":" + resultJson + ",\"notices\":[],\"notice_seq\":0}";

        private static BrowserBridgeResponse Ok(string data)
        {
            using var document = JsonDocument.Parse(data);
            return new BrowserBridgeResponse { Ok = true, Data = document.RootElement.Clone() };
        }

        private static BrowserBridgeResponse Fail(string code) => BrowserBridgeResponse.Failure(code, code, true);
    }

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

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task Click_ResolvesTheRefAndDispatchesRealMouseEvents()
    {
        var harness = new Harness();
        var snapshot = await harness.SnapshotAsync();
        Assert.True(snapshot.Ok, snapshot.Message);
        Assert.Contains("[ref=1]", snapshot.Snapshot);

        var result = await harness.Runtime.ClickAsync("s1", new BrowserClickArgs { Ref = 1 }, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(1, result.Ref);
        var events = harness.InputCalls("Input.dispatchMouseEvent");
        Assert.Equal(3, events.Count);
        Assert.Equal("mouseMoved", events[0].Parameters!.Value.GetProperty("type").GetString());
        Assert.Equal("mousePressed", events[1].Parameters!.Value.GetProperty("type").GetString());
        Assert.Equal("mouseReleased", events[2].Parameters!.Value.GetProperty("type").GetString());
        Assert.Equal(20, events[1].Parameters!.Value.GetProperty("x").GetDouble());
        Assert.Equal(20, events[1].Parameters!.Value.GetProperty("y").GetDouble());
    }

    [Fact]
    public async Task Click_InvalidatesRefsWhenTheDocumentLoaderChanged()
    {
        var harness = new Harness();
        await harness.SnapshotAsync();
        harness.Loader = "L2";

        var result = await harness.Runtime.ClickAsync("s1", new BrowserClickArgs { Ref = 1 }, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("browser_ref_stale", result.Code);
        Assert.True(result.Permanent);
        Assert.Empty(harness.InputCalls("Input.dispatchMouseEvent"));
    }

    [Fact]
    public async Task Click_RejectsAnUnknownRef()
    {
        var harness = new Harness();
        var result = await harness.Runtime.ClickAsync("s1", new BrowserClickArgs { Ref = 99 }, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("browser_ref_unknown", result.Code);
        Assert.Empty(harness.InputCalls("Input.dispatchMouseEvent"));
    }

    [Fact]
    public async Task Click_RefusesAnObscuredElementWithoutDispatchingInput()
    {
        var harness = new Harness { HitOk = false, HitReason = "obscured" };
        await harness.SnapshotAsync();

        var result = await harness.Runtime.ClickAsync("s1", new BrowserClickArgs { Ref = 1 }, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("browser_element_not_actionable", result.Code);
        Assert.False(result.Permanent);
        Assert.Contains("obscured", result.Message);
        Assert.Empty(harness.InputCalls("Input.dispatchMouseEvent"));
    }

    [Fact]
    public async Task Type_FocusesInsertsAndVerifies()
    {
        var harness = new Harness();
        await harness.SnapshotAsync();

        var result = await harness.Runtime.TypeAsync(
            "s1", new BrowserTypeArgs { Ref = 1, Text = "hello" }, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("hello", result.Value);
        Assert.True(result.Verified);
        var insert = Assert.Single(harness.InputCalls("Input.insertText"));
        Assert.Equal("hello", insert.Parameters!.Value.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Type_FallsBackToTheNativeSetterWhenTheInsertedValueDoesNotStick()
    {
        var harness = new Harness { FallbackFirstRead = true };
        await harness.SnapshotAsync();

        var result = await harness.Runtime.TypeAsync(
            "s1", new BrowserTypeArgs { Ref = 1, Text = "hello" }, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("hello", result.Value);
        Assert.True(result.Verified);
        Assert.Contains(
            harness.Bridge.Calls,
            call => call.Method == "Runtime.callFunctionOn"
                    && call.Parameters is { } parameters
                    && parameters.TryGetProperty("functionDeclaration", out var declaration)
                    && declaration.GetString()!.Contains("HTMLInputElement", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Press_DispatchesTheNamedKey()
    {
        var harness = new Harness();
        var result = await harness.Runtime.PressAsync("s1", new BrowserPressArgs { Key = "Enter" }, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        var keys = harness.InputCalls("Input.dispatchKeyEvent");
        Assert.Equal(2, keys.Count);
        Assert.Equal("Enter", keys[0].Parameters!.Value.GetProperty("key").GetString());
        Assert.Equal("\r", keys[0].Parameters!.Value.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Press_RejectsAnUnknownKey()
    {
        var harness = new Harness();
        var result = await harness.Runtime.PressAsync("s1", new BrowserPressArgs { Key = "F13" }, CancellationToken.None);
        Assert.Equal("browser_key_unsupported", result.Code);
    }

    [Fact]
    public async Task Scroll_UsesTheViewportCenterWithoutARef()
    {
        var harness = new Harness();
        var result = await harness.Runtime.ScrollAsync(
            "s1", new BrowserScrollArgs { DeltaY = 400 }, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        var wheel = Assert.Single(harness.InputCalls("Input.dispatchMouseEvent"));
        Assert.Equal("mouseWheel", wheel.Parameters!.Value.GetProperty("type").GetString());
        Assert.Equal(400, wheel.Parameters!.Value.GetProperty("x").GetDouble());
        Assert.Equal(300, wheel.Parameters!.Value.GetProperty("y").GetDouble());
        Assert.Equal(400, wheel.Parameters!.Value.GetProperty("deltaY").GetDouble());
    }

    [Fact]
    public async Task History_ClearsRefsAfterMovingBack()
    {
        var harness = new Harness();
        await harness.SnapshotAsync();

        var result = await harness.Runtime.HistoryAsync("s1", new BrowserHistoryArgs { Action = "back" }, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Contains(harness.Bridge.Calls, call => call.Method == "Page.navigateToHistoryEntry");
        var after = await harness.Runtime.ClickAsync("s1", new BrowserClickArgs { Ref = 1 }, CancellationToken.None);
        Assert.Equal("browser_ref_unknown", after.Code);
    }

    [Fact]
    public async Task TabOpen_SendsTheUrlAndRefreshesTheTabList()
    {
        var harness = new Harness();
        var result = await harness.Runtime.TabOpenAsync(
            "s1", new BrowserTabOpenArgs { Url = "https://example.com/docs" }, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        var open = Assert.Single(harness.Bridge.Calls.Where(call => call.Op == "tabs/open"));
        Assert.Equal("https://example.com/docs", open.Url);
        Assert.Single(result.Tabs!);
    }

    [Fact]
    public async Task Batch_StopsAtTheFirstFailureAndReturnsAFreshSnapshot()
    {
        var harness = new Harness();
        await harness.SnapshotAsync();
        var args = new BrowserBatchArgs
        {
            Steps =
            [
                new BrowserBatchStep { Tool = "browser_click", Args = Json("""{"ref":1}""") },
                new BrowserBatchStep { Tool = "browser_click", Args = Json("""{"ref":99}""") },
                new BrowserBatchStep { Tool = "browser_click", Args = Json("""{"ref":1}""") },
            ],
        };

        var result = await harness.Runtime.BatchAsync("s1", args, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("browser_ref_unknown", result.Code);
        Assert.Equal(2, result.Steps!.Length);
        Assert.True(result.Steps[0].Ok);
        Assert.False(result.Steps[1].Ok);
        Assert.Contains("[ref=1]", result.Snapshot);
        Assert.Single(harness.InputCalls("Input.dispatchMouseEvent").Where(call => call.Parameters!.Value.GetProperty("type").GetString() == "mouseReleased"));
    }

    [Fact]
    public async Task Batch_RejectsAnInvalidStepListBeforeTouchingTheBridge()
    {
        var harness = new Harness();
        var tooMany = new BrowserBatchArgs
        {
            Steps = Enumerable.Range(0, 21)
                .Select(_ => new BrowserBatchStep { Tool = "browser_click", Args = Json("""{"ref":1}""") })
                .ToArray(),
        };
        var invalid = await harness.Runtime.BatchAsync("s1", tooMany, CancellationToken.None);
        Assert.Equal("browser_batch_invalid", invalid.Code);

        var unknown = await harness.Runtime.BatchAsync("s1", new BrowserBatchArgs
        {
            Steps = [new BrowserBatchStep { Tool = "browser_navigate", Args = Json("""{"url":"https://example.com"}""") }],
        }, CancellationToken.None);
        Assert.Equal("browser_batch_invalid", unknown.Code);
        Assert.Empty(harness.Bridge.Calls);
    }
}
