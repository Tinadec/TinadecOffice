using System.Text.Json;
using TinadecTools.Tools.Browser;
using TinadecTools.Tools.FileRW;

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
        long noticeSeq = 0,
        string consoleErrors = "[]")
    {
        const string tab = """[{"id":"t1","url":"https://example.com/","title":"Example","active":true}]""";
        var tabs = withTab ? tab : "[]";
        var active = activeTab is null ? "null" : $"\"{activeTab}\"";
        return $$"""{"control":"{{control}}","tabs":{{tabs}},"active_tab":{{active}},"dialog":{{dialog}},"notices":{{notices}},"notice_seq":{{noticeSeq}},"console_errors":{{consoleErrors}}}""";
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
    public async Task Snapshot_HardStopsWhenTheUserHasControlOrADialogIsOpen_AndDialogToolResolvesIt()
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

        var dialogOpen = true;
        bridge.Calls.Clear();
        bridge.Handler = call => call switch
        {
            { Op: "state" } => Ok(StateJson(dialog: dialogOpen ? """{"type":"prompt","message":"Enter code"}""" : "null")),
            { Op: "dialog" } => ClearAndReturn(),
            { Op: "cdp", Method: "Page.getFrameTree" } => Ok(CdpData(StateFrame)),
            { Op: "cdp", Method: "Accessibility.getFullAXTree" } => Ok(CdpData(AxTree)),
            _ => Fail("browser_bridge_error"),
        };

        BrowserBridgeResponse ClearAndReturn()
        {
            dialogOpen = false;
            return Ok(StateJson());
        }

        var runtime = Runtime(bridge);
        var dialog = await runtime.SnapshotAsync("s2", new BrowserSnapshotArgs(), CancellationToken.None);
        Assert.Equal("browser_dialog_open", dialog.Code);
        Assert.Contains("Enter code", dialog.Message);
        Assert.NotNull(dialog.PendingDialog);
        Assert.Equal("prompt", dialog.PendingDialog!.Type);
        // DIALOG_SAFE_METHOD bypass: Page.getFrameTree / Accessibility.getFullAXTree were NOT sent while dialog was open!
        Assert.DoesNotContain(bridge.Calls, c => c.Op == "cdp");

        var handled = await runtime.DialogAsync("s2", new BrowserDialogArgs { Accept = true, PromptText = "4242" }, CancellationToken.None);
        Assert.True(handled.Ok, handled.Message);
        var dialogCall = Assert.Single(bridge.Calls, c => c.Op == "dialog");
        Assert.True(dialogCall.Accept);
        Assert.Equal("4242", dialogCall.PromptText);

        var resumedSnap = await runtime.SnapshotAsync("s2", new BrowserSnapshotArgs(), CancellationToken.None);
        Assert.True(resumedSnap.Ok, resumedSnap.Message);
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
    public async Task Navigate_OpensTheBrowserOnFirstUseAndAttachesActionReceipt()
    {
        var stateCalls = 0;
        var bridge = new FakeBridge();
        bridge.Handler = call => call switch
        {
            { Op: "state" } => Ok(StateJson(withTab: ++stateCalls > 1, consoleErrors: """["Uncaught TypeError: demo"]""")),
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
        Assert.NotNull(result.Receipt);
        Assert.Single(result.Receipt!.OpenedTabs!);
        Assert.Contains("Uncaught TypeError: demo", result.Receipt.ConsoleErrors!);
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
    public async Task Screenshot_PopulatesBothPathAndMultimodalScreenshotPath()
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
        Assert.Equal("C:/tmp/shot.png", result.ScreenshotPath);
        Assert.Equal(1234, result.Bytes);
        Assert.True(bridge.Calls.Single(call => call.Op == "screenshot").FullPage);
    }

    [Fact]
    public async Task ControlStateMachine_HandoffLocksWrites_TakeoverResumes_AndFinishRefusesSubsequentTakeover()
    {
        var bridge = new FakeBridge();
        bridge.Handler = call => call switch
        {
            { Op: "state" } => Ok(StateJson()),
            { Op: "handoff" } => Ok(StateJson(control: "user")),
            { Op: "takeover" } => Ok(StateJson(control: "agent")),
            { Op: "finish" } => Ok("""{"finished":true,"control":"finished","kept_tabs":["t1"],"closed_tabs":["t2"]}"""),
            { Op: "cdp", Method: "Page.getFrameTree" } => Ok(CdpData(StateFrame)),
            { Op: "cdp", Method: "Accessibility.getFullAXTree" } => Ok(CdpData(AxTree)),
            _ => Fail("browser_bridge_error"),
        };
        var runtime = Runtime(bridge);

        var handoff = await runtime.HandOffAsync("s-ctrl", new BrowserHandoffArgs { Reason = "2fa_required" }, CancellationToken.None);
        Assert.True(handoff.Ok);
        Assert.Equal("browser_handoff_requested", handoff.Code);
        Assert.Equal("user", handoff.Control);
        Assert.Equal("s-ctrl", handoff.SpaceId);

        // Agent calls while handed off are blocked locally without touching CDP.
        var blocked = await runtime.SnapshotAsync("s-ctrl", new BrowserSnapshotArgs(), CancellationToken.None);
        Assert.False(blocked.Ok);
        Assert.Equal("browser_user_in_control", blocked.Code);

        // Takeover restores agent control.
        var takeover = await runtime.TakeOverAsync("s-ctrl", new BrowserTakeoverArgs(), CancellationToken.None);
        Assert.True(takeover.Ok, takeover.Message);
        Assert.Equal("browser_control_resumed", takeover.Code);
        Assert.Equal("agent", takeover.Control);

        // Finish transitions to terminal `finished` state.
        var finished = await runtime.FinishAsync("s-ctrl", new BrowserFinishArgs { Keep = ["t1"] }, CancellationToken.None);
        Assert.True(finished.Ok, finished.Message);
        Assert.Equal("browser_task_space_completed", finished.Code);
        Assert.Equal("finished", finished.Control);
        Assert.Contains("t1", finished.KeptTabs!);

        // Takeover MUST refuse resuming a finished/ended task space cleanly per ego-errors.ts!
        var illegalResume = await runtime.TakeOverAsync("s-ctrl", new BrowserTakeoverArgs(), CancellationToken.None);
        Assert.False(illegalResume.Ok);
        Assert.Equal("browser_task_space_finished", illegalResume.Code);
        Assert.True(illegalResume.Permanent);
        Assert.Contains("EGO_TASK_SPACE_INACTIVE", illegalResume.Message);
    }

    [Fact]
    public async Task TaskSpaceIsolation_SwarmTeammatesGetDisjointSessionNamespaces()
    {
        var bridge = new FakeBridge
        {
            Handler = call => call.Op == "tabs/list"
                ? Ok($"[{{\"id\":\"tab-{call.SessionId}\",\"url\":\"https://example.com/\",\"title\":\"T\",\"active\":true}}]")
                : Fail("browser_bridge_error"),
        };
        var connector = new BuiltinElectronBrowserConnector(Runtime(bridge));

        using var ctxDocA = JsonDocument.Parse("""{"run_id":"run-42","teammate_handle":"researcher"}""");
        using var ctxDocB = JsonDocument.Parse("""{"run_id":"run-42","teammate_handle":"verifier"}""");

        var ctxA = BrowserConnectorContext.Resolve("shared-session", ctxDocA.RootElement, taskSpaceOverride: "docs");
        var ctxB = BrowserConnectorContext.Resolve("shared-session", ctxDocB.RootElement, taskSpaceOverride: "docs");

        Assert.NotEqual(ctxA.NamespaceKey, ctxB.NamespaceKey);
        var resA = await connector.TabsListAsync(ctxA, CancellationToken.None);
        var resB = await connector.TabsListAsync(ctxB, CancellationToken.None);

        Assert.True(resA.Ok);
        Assert.True(resB.Ok);
        Assert.NotEqual(resA.TabId, resB.TabId);
    }

    [Fact]
    public async Task SiteLearningStore_SavesValidWorkspaceSkillAndRejectsTemporaryRefs()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "tinadec-learnings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            Assert.False(BrowserSiteLearningStore.ValidateLearningContent("Click @12 or [ref=4] to submit", out var tempRefErr));
            Assert.Contains("stable locators", tempRefErr, StringComparison.OrdinalIgnoreCase);

            var saved = BrowserSiteLearningStore.Save(
                "https://App.Example.com/login",
                "Login flow notes for app.example.com",
                "Use `loc=role:button[name=\"Sign in\"]` after filling `loc=css:input[name=\"email\"]`.",
                workspaceRootOverride: tempRoot);

            Assert.Equal("app.example.com", saved.Domain);
            Assert.Equal("app-example-com", saved.SkillName);
            Assert.Equal(".tinadec/skills/browser-learnings/app-example-com/SKILL.md", saved.RelativePath);
            Assert.True(File.Exists(saved.FullPath));

            Assert.True(BrowserSiteLearningStore.TryLoad("https://app.example.com/dashboard", out var loaded, workspaceRootOverride: tempRoot));
            Assert.NotNull(loaded);
            Assert.Equal("app.example.com", loaded!.Domain);
            Assert.Single(BrowserSiteLearningStore.List(workspaceRootOverride: tempRoot));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task RunWorkflow_EnforcesNavigationPolicyUpFrontAndExecutesMultiStepInOneTurn()
    {
        var bridge = new FakeBridge();
        var runtime = Runtime(bridge);

        // Step 1 is valid, Step 2 has a blocked metadata URL -> rejected BEFORE Step 1 touches the bridge!
        var blockedRun = await runtime.RunAsync("s-run", new BrowserRunArgs
        {
            Steps =
            [
                new BrowserRunStep { Action = "goto", Url = "https://example.com/" },
                new BrowserRunStep { Action = "goto", Url = "http://169.254.169.254/latest/meta-data" },
            ],
        }, CancellationToken.None);

        Assert.False(blockedRun.Ok);
        Assert.Equal("browser_navigation_blocked", blockedRun.Code);
        Assert.Empty(bridge.Calls);

        // Valid workflow: goto -> extract -> compact snapshot + multimodal screenshot!
        var stateCalls = 0;
        bridge.Handler = call => call switch
        {
            { Op: "state" } => Ok(StateJson(withTab: ++stateCalls > 1)),
            { Op: "tabs/open" } => Ok("""{"tab":{"id":"t1","url":"https://example.com/","title":"Example","active":true},"state":{},"notices":[],"notice_seq":0}"""),
            { Op: "cdp", Method: "Runtime.evaluate" } => Ok(CdpData("""{"result":{"objectId":"window-1"}}""")),
            { Op: "cdp", Method: "Runtime.callFunctionOn" } when call.Parameters!.Value.GetProperty("functionDeclaration").GetString()!.Contains("readyState") =>
                Ok(CdpData("""{"result":{"type":"boolean","value":true}}""")),
            { Op: "cdp", Method: "Runtime.callFunctionOn" } =>
                Ok(CdpData("""{"result":{"value":{"ok":true,"value":"Example Heading"}}}""")),
            { Op: "cdp", Method: "Page.getFrameTree" } => Ok(CdpData(StateFrame)),
            { Op: "cdp", Method: "Page.getLayoutMetrics" } => Fail("browser_bridge_error"),
            { Op: "cdp", Method: "Accessibility.getFullAXTree" } => Ok(CdpData(AxTree)),
            { Op: "screenshot" } => Ok("""{"path":"C:/tmp/workflow.png","bytes":2048,"notices":[],"notice_seq":0}"""),
            _ => Fail("browser_bridge_error"),
        };

        var workflow = await runtime.RunAsync("s-run", new BrowserRunArgs
        {
            Steps =
            [
                new BrowserRunStep { Action = "goto", Url = "https://example.com/", WaitMs = 500 },
                new BrowserRunStep { Action = "extract", Selector = "h1", Name = "heading" },
            ],
            IncludeSnapshot = true,
            IncludeScreenshot = true,
        }, CancellationToken.None);

        Assert.True(workflow.Ok, workflow.Message);
        Assert.Equal(2, workflow.RunSteps!.Length);
        Assert.Single(workflow.Extractions!);
        Assert.Equal("Example Heading", workflow.Extractions![0].Value);
        Assert.Contains("button \"Click me\" [ref=1]", workflow.Snapshot);
        Assert.Equal("C:/tmp/workflow.png", workflow.ScreenshotPath);
        Assert.NotNull(workflow.Receipt);
    }

    [Fact]
    public async Task Upload_EnforcesWorkspaceBoundaryBeforeTouchingBridge()
    {
        var bridge = new FakeBridge();
        var runtime = Runtime(bridge);

        var outside = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("N") + ".txt");
        var denied = await runtime.UploadAsync("s-up", new BrowserUploadArgs { Ref = 1, Path = outside }, CancellationToken.None);

        Assert.False(denied.Ok);
        Assert.Equal("browser_path_denied", denied.Code);
        Assert.Empty(bridge.Calls);

        var insidePath = Path.Combine(WorkspacePathResolver.WorkspaceRoot, $".tmp-upload-test-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(insidePath, "upload fixture");
        try
        {
            bridge.Handler = call => call switch
            {
                { Op: "state" } => Ok(StateJson()),
                { Op: "cdp", Method: "Page.getFrameTree" } => Ok(CdpData(StateFrame)),
                { Op: "cdp", Method: "Accessibility.getFullAXTree" } => Ok(CdpData(AxTree)),
                { Op: "cdp", Method: "DOM.resolveNode" } => Ok(CdpData("""{"object":{"objectId":"input-obj"}}""")),
                { Op: "cdp", Method: "Runtime.callFunctionOn" } => Ok(CdpData("""{"result":{"objectId":"file-input-1"}}""")),
                { Op: "cdp", Method: "DOM.setFileInputFiles" } => Ok(CdpData("{}")),
                _ => Fail("browser_bridge_error"),
            };

            await runtime.SnapshotAsync("s-up", new BrowserSnapshotArgs(), CancellationToken.None);
            var uploaded = await runtime.UploadAsync("s-up", new BrowserUploadArgs { Ref = 1, Path = insidePath }, CancellationToken.None);

            Assert.True(uploaded.Ok, uploaded.Message);
            var setFiles = Assert.Single(bridge.Calls, c => c.Method == "DOM.setFileInputFiles");
            Assert.Contains(insidePath, setFiles.Parameters!.Value.GetProperty("files")[0].GetString());
        }
        finally
        {
            try { File.Delete(insidePath); } catch { /* best effort */ }
        }
    }
}
