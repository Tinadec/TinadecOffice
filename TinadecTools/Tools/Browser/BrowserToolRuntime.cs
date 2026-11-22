using System.Collections.Concurrent;
using System.Text.Json;
using NLog;
using TinadecTools.Runtime;

namespace TinadecTools.Tools.Browser;

internal enum TaskSpaceControlState
{
    Agent,
    User,
    Finished,
}

/// <summary>
/// Session/TaskSpace and tab state for the built-in agent browser, plus observation, dialog-safe
/// CDP routing, cross-iframe/OOPIF snapshots, multimodal screenshots, and control state transitions.
/// </summary>
internal sealed partial class BrowserToolRuntime
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly Lazy<BrowserToolRuntime> LazyDefault = new(
        () => new BrowserToolRuntime(new BrowserBridgeClient(), new BrowserNavigationPolicy()),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private static readonly HashSet<string> HardStopCodes = new(StringComparer.Ordinal)
    {
        "browser_user_in_control",
        "browser_task_space_finished",
        "browser_dialog_open",
        "browser_permission_denied",
        "browser_renderer_gone",
    };

    private static readonly HashSet<string> ActionableHardStops = new(StringComparer.Ordinal)
    {
        "browser_permission_denied",
        "browser_renderer_gone",
    };

    private const string ReadyFunction =
        "async function(timeoutMs){" +
        "const deadline=Date.now()+timeoutMs;" +
        "while(Date.now()<deadline){" +
        "if(document.readyState==='complete')return true;" +
        "await new Promise(function(resolve){setTimeout(resolve,100);});" +
        "}return false;}";

    private const string WaitFunction =
        "async function(text,urlPart,selector,timeoutMs){" +
        "const deadline=Date.now()+timeoutMs;" +
        "while(Date.now()<deadline){" +
        "if(text){const body=document.body?document.body.innerText:'';if(body&&body.indexOf(text)>=0)return {ok:true,reason:'text'};}" +
        "if(urlPart&&location.href.indexOf(urlPart)>=0)return {ok:true,reason:'url'};" +
        "if(selector){try{const el=document.querySelector(selector);if(el)return {ok:true,reason:'selector'};}catch(e){return {ok:false,reason:'invalid_selector'};}}" +
        "await new Promise(function(resolve){setTimeout(resolve,100);});" +
        "}return {ok:false,reason:'timeout'};}";

    private readonly IBrowserBridge _bridge;
    private readonly BrowserNavigationPolicy _navigationPolicy;
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);

    public BrowserToolRuntime(IBrowserBridge bridge, BrowserNavigationPolicy navigationPolicy)
    {
        _bridge = bridge;
        _navigationPolicy = navigationPolicy;
    }

    public static BrowserToolRuntime Default => LazyDefault.Value;

    private static TimeSpan ActionTimeout => TimeSpan.FromMilliseconds(
        ToolExecutionContext.Current?.Integer("browser", "action_timeout_ms", 15_000, 1_000, 60_000) ?? 15_000);

    internal BrowserNavigationPolicy CurrentNavigationPolicy()
    {
        var allowPrivate = ToolExecutionContext.Current?.Boolean("browser", "allow_private_hosts", false) ?? false;
        return allowPrivate ? new BrowserNavigationPolicy(allowPrivate: true) : _navigationPolicy;
    }

    /// <summary>
    /// Only browser-level CDP commands, <c>Page.handleJavaScriptDialog</c>, and
    /// <c>Page.setDownloadBehavior</c> are answered while a JS modal dialog is open;
    /// every other renderer-bound command stalls in Chromium and must be bypassed fast.
    /// </summary>
    internal static bool IsDialogSafeMethod(string? method) =>
        !string.IsNullOrEmpty(method)
        && (method.StartsWith("Target.", StringComparison.Ordinal)
            || method.StartsWith("Browser.", StringComparison.Ordinal)
            || string.Equals(method, "Page.handleJavaScriptDialog", StringComparison.Ordinal)
            || string.Equals(method, "Page.setDownloadBehavior", StringComparison.Ordinal));

    // ------------------------------------------------------------------ observation + navigation

    public async Task<BrowserToolResult> SnapshotAsync(string? sessionId, BrowserSnapshotArgs args, CancellationToken cancellationToken)
    {
        var id = NormalizeSessionId(sessionId);
        if (id is null) return Fail("browser_session_invalid", "A session id is required.", permanent: true);
        var session = GetSession(id);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SnapshotLockedAsync(session, args, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async Task<BrowserToolResult> SnapshotLockedAsync(
        SessionState session,
        BrowserSnapshotArgs args,
        CancellationToken cancellationToken)
    {
        var scope = NormalizeScope(args.Scope);
        if (scope is null)
        {
            return Fail("browser_snapshot_invalid", "scope must be full_page, viewport, or subtree.", permanent: true);
        }
        if (scope == "subtree" && (args.RootRef is null || args.RootRef <= 0))
        {
            return Fail("browser_snapshot_invalid", "root_ref is required when scope is subtree.", permanent: true);
        }

        var refresh = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
        if (refresh.Failure is not null) return refresh.Failure;

        var missing = RequireActiveTab(session, out var tab);
        var notices = refresh.Notices;
        if (missing is not null) return WithNotices(missing, notices);

        var frame = await CdpAsync(session, tab, "Page.getFrameTree", EmptyObject, ActionTimeout, cancellationToken).ConfigureAwait(false);
        if (frame.Failure is not null) return WithNotices(frame.Failure, MergeNotices(notices, frame.Notices));
        notices = MergeNotices(notices, frame.Notices);

        var loaderId = GetFrameLoaderId(frame.Root);
        var rootFrameId = GetFrameId(frame.Root);
        if (!string.IsNullOrEmpty(loaderId) && !string.Equals(loaderId, tab.LoaderId, StringComparison.Ordinal))
        {
            tab.Refs.Clear();
            tab.NextRef = 1;
            tab.LoaderId = loaderId;
            tab.FrameId = rootFrameId;
        }
        else if (tab.LoaderId is null)
        {
            tab.LoaderId = loaderId;
            tab.FrameId = rootFrameId;
        }

        FrameNodeKey? subtreeRoot = null;
        string? subtreeFrameId = null;
        if (scope == "subtree")
        {
            var wantedRefId = args.RootRef!.Value;
            if (!tab.Refs.TryGetValue(wantedRefId, out var rootTarget))
            {
                return WithNotices(
                    Fail("browser_ref_unknown", $"Unknown subtree root_ref {wantedRefId}; take a full_page or viewport snapshot first.", permanent: true),
                    notices);
            }
            if (!string.IsNullOrEmpty(rootTarget.ChildFrameId))
            {
                subtreeFrameId = rootTarget.ChildFrameId;
            }
            else
            {
                subtreeRoot = FrameNodeKey.For(rootTarget.FrameId, rootTarget.BackendNodeId);
            }
        }

        var ax = await CdpAsync(
            session, tab, "Accessibility.getFullAXTree", EmptyObject, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (ax.Failure is not null) return WithNotices(ax.Failure, MergeNotices(notices, ax.Notices));
        notices = MergeNotices(notices, ax.Notices);

        var frameInputs = new List<BrowserFrameAxInput>
        {
            new(FrameId: null, CdpSessionId: null, AxTree: ax.Root),
        };

        // Collect child frames (same-origin iframes and cross-origin OOPIFs) without focusing or reloading.
        var childDescriptors = CollectChildFrames(frame.Root, rootFrameId);
        if (childDescriptors.Count > 0)
        {
            var oopifSessions = await DiscoverOopifSessionsAsync(session, tab, childDescriptors, cancellationToken).ConfigureAwait(false);
            foreach (var child in childDescriptors)
            {
                var ownerBackendId = await TryGetFrameOwnerBackendIdAsync(session, tab, child.FrameId, cancellationToken).ConfigureAwait(false);
                var parentNormalized = string.Equals(child.ParentFrameId, rootFrameId, StringComparison.Ordinal)
                    ? null
                    : child.ParentFrameId;

                JsonElement? childAx = null;
                string? cdpSessionId = null;
                if (oopifSessions.TryGetValue(child.FrameId, out var attachedSessionId))
                {
                    var oopifAx = await CdpAsync(
                        session,
                        tab,
                        "Accessibility.getFullAXTree",
                        EmptyObject,
                        TimeSpan.FromSeconds(15),
                        cancellationToken,
                        cdpSessionId: attachedSessionId).ConfigureAwait(false);
                    if (oopifAx.Failure is null && oopifAx.Root.TryGetProperty("nodes", out _))
                    {
                        childAx = oopifAx.Root;
                        cdpSessionId = attachedSessionId;
                        notices = MergeNotices(notices, oopifAx.Notices);
                    }
                }

                if (childAx is null)
                {
                    var sameOriginAx = await CdpAsync(
                        session,
                        tab,
                        "Accessibility.getFullAXTree",
                        WriteObject(w => w.WriteString("frameId", child.FrameId)),
                        TimeSpan.FromSeconds(15),
                        cancellationToken).ConfigureAwait(false);
                    if (sameOriginAx.Failure is null && sameOriginAx.Root.TryGetProperty("nodes", out _))
                    {
                        childAx = sameOriginAx.Root;
                        notices = MergeNotices(notices, sameOriginAx.Notices);
                    }
                }

                if (childAx is { } validChildAx)
                {
                    frameInputs.Add(new BrowserFrameAxInput(
                        FrameId: child.FrameId,
                        CdpSessionId: cdpSessionId,
                        AxTree: validChildAx,
                        ParentFrameId: parentNormalized,
                        OwnerBackendNodeId: ownerBackendId,
                        FrameUrl: child.Url,
                        FrameTitle: child.Name));
                }
            }
        }

        HashSet<FrameNodeKey>? viewportNodes = null;
        var hasBelowViewport = false;
        if (scope == "viewport")
        {
            var vp = await ComputeViewportNodesAsync(session, tab, frameInputs, cancellationToken).ConfigureAwait(false);
            viewportNodes = vp.VisibleNodes;
            hasBelowViewport = vp.HasBelowViewport;
        }

        // Best-effort CSS fallback locator discovery & DOM verification when AX role+name is ambiguous or unnamed.
        var verifiedCss = await TryVerifyCandidateCssLocatorsAsync(session, tab, frameInputs, cancellationToken).ConfigureAwait(false);

        var maxChars = args.MaxChars
            ?? ToolExecutionContext.Current?.Integer(
                "browser", "max_snapshot_chars", BrowserSnapshotBuilder.DefaultMaxChars,
                BrowserSnapshotBuilder.MinimumMaxChars, BrowserSnapshotBuilder.MaximumMaxChars)
            ?? BrowserSnapshotBuilder.DefaultMaxChars;

        var built = BrowserSnapshotBuilder.BuildMultiFrame(
            frameInputs,
            tab.Refs,
            tab.NextRef,
            new BrowserSnapshotOptions
            {
                MaxChars = maxChars,
                Scope = scope,
                SubtreeRoot = subtreeRoot,
                SubtreeFrameId = subtreeFrameId,
                ViewportNodes = viewportNodes,
                HasBelowViewportContent = hasBelowViewport,
                VerifiedCssLocators = verifiedCss,
                IncludeLocators = true,
            });

        if (scope == "subtree" && !built.SubtreeRootFound)
        {
            return WithNotices(
                Fail("browser_ref_stale", $"Subtree root_ref {args.RootRef} is no longer present in the current document.", permanent: true),
                notices);
        }

        // Subtree snapshots merge/update refs without wiping top-level refs from the same document;
        // full_page and viewport snapshots replace the active ref map for this document.
        if (scope == "subtree")
        {
            foreach (var kvp in built.Refs)
            {
                tab.Refs[kvp.Key] = kvp.Value;
            }
        }
        else
        {
            tab.Refs = built.Refs;
        }
        tab.NextRef = built.NextRef;

        var result = new BrowserToolResult
        {
            Ok = true,
            SpaceId = session.Id,
            Control = ControlName(session.Control),
            TabId = tab.Id,
            Url = tab.Url,
            Title = tab.Title,
            Snapshot = built.Text,
            Truncated = built.Truncated,
            RefCount = built.RefCount,
            SiteLearningPath = TryFindSiteLearningPath(tab.Url),
        };
        return WithNotices(result, notices);
    }

    public async Task<BrowserToolResult> NavigateAsync(string? sessionId, BrowserNavigateArgs args, CancellationToken cancellationToken)
    {
        var id = NormalizeSessionId(sessionId);
        if (id is null) return Fail("browser_session_invalid", "A session id is required.", permanent: true);
        if (!CurrentNavigationPolicy().TryNormalize(args.Url, out var url, out var error))
        {
            return Fail("browser_navigation_blocked", error, permanent: true);
        }

        var session = GetSession(id);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await NavigateLockedAsync(session, url, args.WaitMs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async Task<BrowserToolResult> NavigateLockedAsync(
        SessionState session,
        Uri url,
        int? requestedWaitMs,
        CancellationToken cancellationToken)
    {
        var consoleBaseline = session.ConsoleErrorCount;
        var refresh = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
        if (refresh.Failure is not null) return refresh.Failure;

        var beforeTabs = SnapshotTabIds(session);
        var notices = refresh.Notices;

        if (session.ActiveTabId is null || !session.Tabs.ContainsKey(session.ActiveTabId))
        {
            var opened = await _bridge.SendAsync(
                new BrowserBridgeCall("tabs/open", session.Id, Url: url.ToString()),
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);
            if (!opened.Ok) return FromBridge(session, opened);
            RecordConsoleErrors(session, opened.Data);
            notices = MergeNotices(notices, CollectNewNotices(session, opened.Data ?? EmptyObject));

            var afterOpen = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
            if (afterOpen.Failure is not null) return AttachReceipt(session, afterOpen.Failure, beforeTabs, consoleBaseline);
            notices = MergeNotices(notices, afterOpen.Notices);
        }
        else
        {
            var active = session.Tabs[session.ActiveTabId];
            var navigate = await CdpAsync(
                session,
                active,
                "Page.navigate",
                WriteObject(writer => writer.WriteString("url", url.ToString())),
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);
            if (navigate.Failure is not null)
            {
                return AttachReceipt(session, WithNotices(navigate.Failure, MergeNotices(notices, navigate.Notices)), beforeTabs, consoleBaseline);
            }
            notices = MergeNotices(notices, navigate.Notices);
            active.Refs.Clear();
            active.NextRef = 1;
            active.LoaderId = null;
        }

        var missing = RequireActiveTab(session, out var tab);
        if (missing is not null) return AttachReceipt(session, WithNotices(missing, notices), beforeTabs, consoleBaseline);

        var waitMs = Math.Clamp(requestedWaitMs ?? 10_000, 0, 30_000);
        var ready = true;
        if (waitMs > 0)
        {
            var readyResult = await WaitForReadyAsync(session, tab, waitMs, cancellationToken).ConfigureAwait(false);
            if (readyResult.Failure is not null)
            {
                return AttachReceipt(session, WithNotices(readyResult.Failure, MergeNotices(notices, readyResult.Notices)), beforeTabs, consoleBaseline);
            }
            notices = MergeNotices(notices, readyResult.Notices);
            ready = readyResult.Ready;
        }

        var finalRefresh = await RefreshAsync(session, cancellationToken, allowOpenDialog: true).ConfigureAwait(false);
        if (finalRefresh.Failure is not null) return AttachReceipt(session, finalRefresh.Failure, beforeTabs, consoleBaseline);
        notices = MergeNotices(notices, finalRefresh.Notices);

        var result = new BrowserToolResult
        {
            Ok = true,
            SpaceId = session.Id,
            Control = ControlName(session.Control),
            TabId = tab.Id,
            Url = tab.Url,
            Title = tab.Title,
            Ready = ready,
            SiteLearningPath = TryFindSiteLearningPath(tab.Url),
        };
        return AttachReceipt(session, WithNotices(result, notices), beforeTabs, consoleBaseline);
    }

    public async Task<BrowserToolResult> TabsListAsync(string? sessionId, CancellationToken cancellationToken)
    {
        var id = NormalizeSessionId(sessionId);
        if (id is null) return Fail("browser_session_invalid", "A session id is required.", permanent: true);
        var session = GetSession(id);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var guard = CheckLocalControlBoundary(session);
            if (guard is not null) return guard;

            var response = await _bridge.SendAsync(
                new BrowserBridgeCall("tabs/list", session.Id),
                ActionTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!response.Ok) return FromBridge(session, response);

            var tabs = ParseTabs(response.Data ?? EmptyObject);
            SyncTabs(session, tabs, activeTabId: null);
            return new BrowserToolResult
            {
                Ok = true,
                SpaceId = session.Id,
                Control = ControlName(session.Control),
                Tabs = tabs.Select(tab => new BrowserTabInfo
                {
                    Id = tab.Id,
                    Url = tab.Url,
                    Title = tab.Title,
                    Active = tab.Active,
                }).ToArray(),
                TabId = session.ActiveTabId,
            };
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> WaitAsync(string? sessionId, BrowserWaitArgs args, CancellationToken cancellationToken)
    {
        var id = NormalizeSessionId(sessionId);
        if (id is null) return Fail("browser_session_invalid", "A session id is required.", permanent: true);
        var session = GetSession(id);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await WaitLockedAsync(session, args, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async Task<BrowserToolResult> WaitLockedAsync(
        SessionState session,
        BrowserWaitArgs args,
        CancellationToken cancellationToken)
    {
        var text = string.IsNullOrWhiteSpace(args.Text) ? null : args.Text;
        var urlPart = string.IsNullOrWhiteSpace(args.UrlContains) ? null : args.UrlContains;
        var selector = string.IsNullOrWhiteSpace(args.Selector) ? null : StripCssLocatorPrefix(args.Selector);
        if (text is null && urlPart is null && selector is null)
        {
            return Fail("browser_wait_invalid", "Provide text, url_contains, or selector to wait for.", permanent: true);
        }

        var timeoutMs = Math.Clamp(
            args.TimeoutMs
                ?? ToolExecutionContext.Current?.Integer("browser", "wait_timeout_ms", 15_000, 100, 60_000)
                ?? 15_000,
            100,
            60_000);

        var refresh = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
        if (refresh.Failure is not null) return refresh.Failure;
        var missing = RequireActiveTab(session, out var tab);
        if (missing is not null) return WithNotices(missing, refresh.Notices);

        var objectId = await GetWindowObjectIdAsync(session, tab, cancellationToken).ConfigureAwait(false);
        if (objectId.Failure is not null) return WithNotices(objectId.Failure, MergeNotices(refresh.Notices, objectId.Notices));
        if (objectId.ObjectId is null)
        {
            return Fail("browser_wait_failed", "Could not reach the page's execution context.", permanent: false);
        }

        var call = await CdpAsync(
            session,
            tab,
            "Runtime.callFunctionOn",
            WriteObject(writer =>
            {
                writer.WriteString("objectId", objectId.ObjectId);
                writer.WriteString("functionDeclaration", WaitFunction);
                writer.WriteBoolean("awaitPromise", true);
                writer.WriteBoolean("returnByValue", true);
                writer.WritePropertyName("arguments");
                writer.WriteStartArray();
                WriteRemoteString(writer, text);
                WriteRemoteString(writer, urlPart);
                WriteRemoteString(writer, selector);
                writer.WriteStartObject();
                writer.WriteNumber("value", timeoutMs);
                writer.WriteEndObject();
                writer.WriteEndArray();
            }),
            TimeSpan.FromMilliseconds(timeoutMs + 15_000),
            cancellationToken).ConfigureAwait(false);
        if (call.Failure is not null) return WithNotices(call.Failure, MergeNotices(refresh.Notices, call.Notices));

        var value = GetByValue(call.Root);
        var ok = value is { ValueKind: JsonValueKind.Object }
                 && value.Value.TryGetProperty("ok", out var okElement)
                 && okElement.ValueKind == JsonValueKind.True;
        var reason = value is { ValueKind: JsonValueKind.Object }
                     && value.Value.TryGetProperty("reason", out var reasonElement)
            ? reasonElement.GetString()
            : null;
        if (!ok && string.Equals(reason, "invalid_selector", StringComparison.Ordinal))
        {
            return WithNotices(
                Fail("browser_selector_invalid", $"Invalid CSS selector '{selector}'.", permanent: true),
                MergeNotices(refresh.Notices, call.Notices));
        }
        var result = ok
            ? new BrowserToolResult { Ok = true, SpaceId = session.Id, TabId = tab.Id, Reason = reason }
            : Fail("browser_wait_timeout", $"The condition did not appear within {timeoutMs} ms.", permanent: false);
        return WithNotices(result, MergeNotices(refresh.Notices, call.Notices));
    }

    public async Task<BrowserToolResult> ScreenshotAsync(string? sessionId, BrowserScreenshotArgs args, CancellationToken cancellationToken)
    {
        var id = NormalizeSessionId(sessionId);
        if (id is null) return Fail("browser_session_invalid", "A session id is required.", permanent: true);
        var session = GetSession(id);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ScreenshotLockedAsync(session, args, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async Task<BrowserToolResult> ScreenshotLockedAsync(
        SessionState session,
        BrowserScreenshotArgs args,
        CancellationToken cancellationToken)
    {
        var refresh = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
        if (refresh.Failure is not null) return refresh.Failure;
        var missing = RequireActiveTab(session, out var tab);
        if (missing is not null) return WithNotices(missing, refresh.Notices);

        if (session.PendingDialog is { } openDialog)
        {
            return FailDialogOpen(session, openDialog, refresh.Notices);
        }

        var response = await _bridge.SendAsync(
            new BrowserBridgeCall("screenshot", session.Id, tab.Id, FullPage: args.FullPage == true),
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        if (!response.Ok) return FromBridge(session, response);

        var data = response.Data ?? EmptyObject;
        RecordConsoleErrors(session, data);
        var shotPath = GetString(data, "path");
        var result = new BrowserToolResult
        {
            Ok = true,
            SpaceId = session.Id,
            Control = ControlName(session.Control),
            TabId = tab.Id,
            Url = tab.Url,
            Title = tab.Title,
            Path = shotPath,
            ScreenshotPath = shotPath,
            Bytes = GetInt64(data, "bytes"),
        };
        var notices = MergeNotices(refresh.Notices, CollectNewNotices(session, data));
        return WithNotices(result, notices);
    }

    // ------------------------------------------------------------------ state + dialog-safe CDP

    private sealed class SessionState
    {
        public string Id { get; init; } = string.Empty;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Dictionary<string, TabState> Tabs { get; } = new(StringComparer.Ordinal);
        public string? ActiveTabId { get; set; }
        public long LastNoticeSeq { get; set; }
        public TaskSpaceControlState Control { get; set; } = TaskSpaceControlState.Agent;
        public BrowserDialogInfo? PendingDialog { get; set; }
        public List<string> ConsoleErrors { get; } = new();
        public int ConsoleErrorCount => ConsoleErrors.Count;
    }

    private sealed class TabState
    {
        public string Id { get; init; } = string.Empty;
        public string? Url { get; set; }
        public string? Title { get; set; }
        public string? LoaderId { get; set; }
        public string? FrameId { get; set; }
        public Dictionary<int, BrowserRef> Refs { get; set; } = new();
        public int NextRef { get; set; } = 1;
    }

    private sealed record ChildFrameDescriptor(string FrameId, string? ParentFrameId, string? Url, string? Name);

    private readonly record struct RefreshResult(BrowserToolResult? Failure, BrowserNoticeInfo[] Notices);

    private readonly record struct CdpResult(JsonElement Root, BrowserToolResult? Failure, BrowserNoticeInfo[] Notices);

    private readonly record struct ObjectIdResult(string? ObjectId, BrowserToolResult? Failure, BrowserNoticeInfo[] Notices);

    private readonly record struct ReadyResult(bool Ready, BrowserToolResult? Failure, BrowserNoticeInfo[] Notices);

    private SessionState GetSession(string sessionId) =>
        _sessions.GetOrAdd(sessionId, key => new SessionState { Id = key });

    private BrowserToolResult? CheckLocalControlBoundary(SessionState session, bool allowUserControl = false)
    {
        if (session.Control == TaskSpaceControlState.Finished)
        {
            return FailTaskSpaceFinished(session.Id);
        }
        if (!allowUserControl && session.Control == TaskSpaceControlState.User)
        {
            return FailUserInControl(session.Id);
        }
        return null;
    }

    private async Task<RefreshResult> RefreshAsync(
        SessionState session,
        CancellationToken cancellationToken,
        bool allowOpenDialog = false,
        bool allowUserControl = false)
    {
        var boundary = CheckLocalControlBoundary(session, allowUserControl);
        if (boundary is not null) return new RefreshResult(boundary, Array.Empty<BrowserNoticeInfo>());

        var response = await _bridge.SendAsync(
            new BrowserBridgeCall("state", session.Id),
            ActionTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!response.Ok) return new RefreshResult(FromBridge(session, response), Array.Empty<BrowserNoticeInfo>());

        var data = response.Data ?? EmptyObject;
        RecordConsoleErrors(session, data);
        var notices = CollectNewNotices(session, data);
        var control = GetString(data, "control");
        if (string.Equals(control, "finished", StringComparison.OrdinalIgnoreCase))
        {
            session.Control = TaskSpaceControlState.Finished;
            return new RefreshResult(FailTaskSpaceFinished(session.Id, notices), notices);
        }
        if (string.Equals(control, "user", StringComparison.OrdinalIgnoreCase))
        {
            session.Control = TaskSpaceControlState.User;
            if (!allowUserControl)
            {
                return new RefreshResult(FailUserInControl(session.Id, notices), notices);
            }
        }
        else if (string.Equals(control, "agent", StringComparison.OrdinalIgnoreCase) && session.Control != TaskSpaceControlState.Finished)
        {
            // Keep local User handoff lock if agent explicitly called HandOffAsync on a mock bridge that returns static 'agent'.
            // Only TakeOverAsync clears a local handoff lock.
        }

        if (data.TryGetProperty("dialog", out var dialog) && dialog.ValueKind == JsonValueKind.Object)
        {
            session.PendingDialog = ParseDialogElement(dialog);
            if (!allowOpenDialog)
            {
                return new RefreshResult(FailDialogOpen(session, session.PendingDialog, notices), notices);
            }
        }
        else
        {
            session.PendingDialog = null;
        }

        SyncTabs(session, ParseTabs(data), GetString(data, "active_tab"));
        return new RefreshResult(null, notices);
    }

    private async Task<CdpResult> CdpAsync(
        SessionState session,
        TabState tab,
        string method,
        JsonElement parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? cdpSessionId = null)
    {
        // DIALOG_SAFE_METHOD bypass: never let an open JS dialog stall renderer CDP commands into a timeout.
        if (session.PendingDialog is { } dialog && !IsDialogSafeMethod(method))
        {
            return new CdpResult(EmptyObject, FailDialogOpen(session, dialog, Array.Empty<BrowserNoticeInfo>()), Array.Empty<BrowserNoticeInfo>());
        }

        Logger.Debug("browser cdp {Method}", method);
        var response = await _bridge.SendAsync(
            new BrowserBridgeCall("cdp", session.Id, tab.Id, method, parameters, CdpSessionId: cdpSessionId),
            timeout,
            cancellationToken).ConfigureAwait(false);
        if (!response.Ok)
        {
            if (response.PendingDialog is { } pendingFromBridge)
            {
                session.PendingDialog = pendingFromBridge;
            }
            return new CdpResult(EmptyObject, FromBridge(session, response), Array.Empty<BrowserNoticeInfo>());
        }

        var data = response.Data ?? EmptyObject;
        RecordConsoleErrors(session, data);
        if (response.PendingDialog is { } pending)
        {
            session.PendingDialog = pending;
        }
        var notices = CollectNewNotices(session, data);
        var failure = HardStopFailure(session, notices);
        var root = data.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
            ? result.Clone()
            : EmptyObject;
        return new CdpResult(root, failure, notices);
    }

    private async Task<ObjectIdResult> GetWindowObjectIdAsync(
        SessionState session,
        TabState tab,
        CancellationToken cancellationToken,
        string? cdpSessionId = null)
    {
        var window = await CdpAsync(
            session,
            tab,
            "Runtime.evaluate",
            WriteObject(writer =>
            {
                writer.WriteString("expression", "window");
                writer.WriteBoolean("returnByValue", false);
            }),
            ActionTimeout,
            cancellationToken,
            cdpSessionId: cdpSessionId).ConfigureAwait(false);
        if (window.Failure is not null) return new ObjectIdResult(null, window.Failure, window.Notices);
        return new ObjectIdResult(GetObjectId(window.Root), null, window.Notices);
    }

    private async Task<ReadyResult> WaitForReadyAsync(SessionState session, TabState tab, int timeoutMs, CancellationToken cancellationToken)
    {
        var window = await GetWindowObjectIdAsync(session, tab, cancellationToken).ConfigureAwait(false);
        if (window.Failure is not null) return new ReadyResult(false, window.Failure, window.Notices);
        if (window.ObjectId is null) return new ReadyResult(false, null, window.Notices);

        var call = await CdpAsync(
            session,
            tab,
            "Runtime.callFunctionOn",
            WriteObject(writer =>
            {
                writer.WriteString("objectId", window.ObjectId);
                writer.WriteString("functionDeclaration", ReadyFunction);
                writer.WriteBoolean("awaitPromise", true);
                writer.WriteBoolean("returnByValue", true);
                writer.WritePropertyName("arguments");
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WriteNumber("value", timeoutMs);
                writer.WriteEndObject();
                writer.WriteEndArray();
            }),
            TimeSpan.FromMilliseconds(timeoutMs + 15_000),
            cancellationToken).ConfigureAwait(false);
        if (call.Failure is not null) return new ReadyResult(false, call.Failure, MergeNotices(window.Notices, call.Notices));
        var value = GetByValue(call.Root);
        var ready = value is { ValueKind: JsonValueKind.True };
        return new ReadyResult(ready, null, MergeNotices(window.Notices, call.Notices));
    }

    // ------------------------------------------------------------------ multi-frame & viewport helpers

    private static List<ChildFrameDescriptor> CollectChildFrames(JsonElement frameTreeRoot, string? rootFrameId)
    {
        var list = new List<ChildFrameDescriptor>();
        if (!frameTreeRoot.TryGetProperty("frameTree", out var tree) || tree.ValueKind != JsonValueKind.Object)
        {
            return list;
        }

        void Visit(JsonElement node, string? parentId)
        {
            string? currentId = parentId;
            if (node.TryGetProperty("frame", out var f) && f.ValueKind == JsonValueKind.Object)
            {
                var id = GetString(f, "id");
                if (!string.IsNullOrEmpty(id))
                {
                    currentId = id;
                    if (!string.Equals(id, rootFrameId, StringComparison.Ordinal))
                    {
                        var reportedParent = GetString(f, "parentId") ?? parentId;
                        list.Add(new ChildFrameDescriptor(id, reportedParent, GetString(f, "url"), GetString(f, "name")));
                    }
                }
            }
            if (node.TryGetProperty("childFrames", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in children.EnumerateArray())
                {
                    if (child.ValueKind == JsonValueKind.Object) Visit(child, currentId);
                }
            }
        }

        Visit(tree, null);
        return list;
    }

    private async Task<Dictionary<string, string>> DiscoverOopifSessionsAsync(
        SessionState session,
        TabState tab,
        IReadOnlyList<ChildFrameDescriptor> childFrames,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var targets = await CdpAsync(session, tab, "Target.getTargets", EmptyObject, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        if (targets.Failure is not null
            || !targets.Root.TryGetProperty("targetInfos", out var targetInfos)
            || targetInfos.ValueKind != JsonValueKind.Array)
        {
            return map;
        }

        var knownFrameIds = new HashSet<string>(childFrames.Select(c => c.FrameId), StringComparer.Ordinal);
        foreach (var info in targetInfos.EnumerateArray())
        {
            if (info.ValueKind != JsonValueKind.Object) continue;
            var type = GetString(info, "type");
            var targetId = GetString(info, "targetId");
            if (!string.Equals(type, "iframe", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(targetId))
            {
                continue;
            }

            // In Chromium CDP, an OOPIF target's targetId equals its frameId (or carries url matching the child frame).
            string? matchedFrameId = knownFrameIds.Contains(targetId)
                ? targetId
                : childFrames.FirstOrDefault(c => !string.IsNullOrEmpty(c.Url) && string.Equals(c.Url, GetString(info, "url"), StringComparison.Ordinal))?.FrameId;
            if (matchedFrameId is null) continue;

            var attached = await CdpAsync(
                session,
                tab,
                "Target.attachToTarget",
                WriteObject(w =>
                {
                    w.WriteString("targetId", targetId);
                    w.WriteBoolean("flatten", true);
                }),
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);
            var attachedSessionId = GetString(attached.Root, "sessionId");
            if (!string.IsNullOrEmpty(attachedSessionId))
            {
                map[matchedFrameId] = attachedSessionId;
                _ = await CdpAsync(session, tab, "DOM.enable", EmptyObject, TimeSpan.FromSeconds(3), cancellationToken, attachedSessionId).ConfigureAwait(false);
                _ = await CdpAsync(session, tab, "Accessibility.enable", EmptyObject, TimeSpan.FromSeconds(3), cancellationToken, attachedSessionId).ConfigureAwait(false);
            }
        }
        return map;
    }

    private async Task<int?> TryGetFrameOwnerBackendIdAsync(
        SessionState session,
        TabState tab,
        string frameId,
        CancellationToken cancellationToken)
    {
        var owner = await CdpAsync(
            session,
            tab,
            "DOM.getFrameOwner",
            WriteObject(w => w.WriteString("frameId", frameId)),
            TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);
        if (owner.Failure is not null) return null;
        return owner.Root.TryGetProperty("backendNodeId", out var b) && b.TryGetInt32(out var id) && id > 0
            ? id
            : null;
    }

    private async Task<(HashSet<FrameNodeKey>? VisibleNodes, bool HasBelowViewport)> ComputeViewportNodesAsync(
        SessionState session,
        TabState tab,
        IReadOnlyList<BrowserFrameAxInput> frameInputs,
        CancellationToken cancellationToken)
    {
        var metrics = await CdpAsync(session, tab, "Page.getLayoutMetrics", EmptyObject, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        double viewportWidth = 1280;
        double viewportHeight = 800;
        if (metrics.Failure is null)
        {
            if (metrics.Root.TryGetProperty("cssLayoutViewport", out var cssVp) && cssVp.ValueKind == JsonValueKind.Object)
            {
                if (cssVp.TryGetProperty("clientWidth", out var cw) && cw.TryGetDouble(out var w) && w > 0) viewportWidth = w;
                if (cssVp.TryGetProperty("clientHeight", out var ch) && ch.TryGetDouble(out var h) && h > 0) viewportHeight = h;
            }
        }
        else
        {
            return (null, false);
        }

        var visible = new HashSet<FrameNodeKey>();
        var belowFold = false;
        foreach (var frameInput in frameInputs)
        {
            if (!frameInput.AxTree.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array) continue;
            foreach (var node in nodes.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object) continue;
                if (!node.TryGetProperty("backendDOMNodeId", out var bEl) || !bEl.TryGetInt32(out var backendId) || backendId <= 0) continue;

                var box = await CdpAsync(
                    session,
                    tab,
                    "DOM.getBoxModel",
                    WriteObject(w => w.WriteNumber("backendNodeId", backendId)),
                    TimeSpan.FromSeconds(3),
                    cancellationToken,
                    cdpSessionId: frameInput.CdpSessionId).ConfigureAwait(false);
                if (box.Failure is not null || !TryGetBoxBounds(box.Root, out var minX, out var minY, out var maxX, out var maxY))
                {
                    continue;
                }

                if (maxY > viewportHeight + 4) belowFold = true;
                if (maxX > 0 && minX < viewportWidth && maxY > 0 && minY < viewportHeight)
                {
                    visible.Add(FrameNodeKey.For(frameInput.FrameId, backendId));
                }
            }
        }
        return (visible, belowFold);
    }

    private async Task<Dictionary<FrameNodeKey, VerifiedDomLocator>?> TryVerifyCandidateCssLocatorsAsync(
        SessionState session,
        TabState tab,
        IReadOnlyList<BrowserFrameAxInput> frameInputs,
        CancellationToken cancellationToken)
    {
        // Collect only actionable AX nodes whose (role, name) is NOT already globally unique across AX trees
        // (e.g., unnamed inputs or duplicate-named buttons), avoiding unnecessary CDP round-trips on simple trees!
        var roleNameCounts = new Dictionary<(string Role, string Name), int>();
        var candidatesToInspect = new List<(BrowserFrameAxInput Frame, int BackendNodeId, string Role, string Name)>();

        foreach (var frame in frameInputs)
        {
            if (!frame.AxTree.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array) continue;
            foreach (var node in nodes.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object) continue;
                var role = AxScalar(node, "role");
                var name = BrowserSnapshotBuilder.NormalizeRoleNameForKey(AxScalar(node, "name"));
                if (!string.IsNullOrEmpty(role) && !string.IsNullOrEmpty(name))
                {
                    var key = (role.ToLowerInvariant(), name);
                    roleNameCounts[key] = roleNameCounts.GetValueOrDefault(key) + 1;
                }

                var ignored = node.TryGetProperty("ignored", out var ig) && ig.ValueKind == JsonValueKind.True;
                if (!ignored
                    && !string.IsNullOrEmpty(role)
                    && node.TryGetProperty("backendDOMNodeId", out var bEl)
                    && bEl.TryGetInt32(out var backendId)
                    && backendId > 0)
                {
                    candidatesToInspect.Add((frame, backendId, role, name));
                }
            }
        }

        var needingCss = candidatesToInspect
            .Where(c => string.IsNullOrEmpty(c.Name) || roleNameCounts.GetValueOrDefault((c.Role.ToLowerInvariant(), c.Name)) > 1)
            .Take(25)
            .ToList();
        if (needingCss.Count == 0) return null;

        var verified = new Dictionary<FrameNodeKey, VerifiedDomLocator>();
        foreach (var item in needingCss)
        {
            var described = await CdpAsync(
                session,
                tab,
                "DOM.describeNode",
                WriteObject(w =>
                {
                    w.WriteNumber("backendNodeId", item.BackendNodeId);
                    w.WriteNumber("depth", 0);
                }),
                TimeSpan.FromSeconds(3),
                cancellationToken,
                cdpSessionId: item.Frame.CdpSessionId).ConfigureAwait(false);
            if (described.Failure is not null) break; // Bridge does not mock/support DOM.describeNode; skip cleanly.
            if (!described.Root.TryGetProperty("node", out var domNode) || domNode.ValueKind != JsonValueKind.Object) continue;

            var tagName = GetString(domNode, "localName") ?? GetString(domNode, "nodeName");
            var attrs = ParseDomAttributes(domNode);
            attrs.TryGetValue("type", out var inputType);
            attrs.TryGetValue("role", out var explicitRole);

            if (!BrowserSnapshotBuilder.IsElementCompatibleWithRole(item.Role, tagName, inputType, explicitRole))
            {
                continue;
            }

            var cssSelector = BuildCandidateCssSelector(tagName, attrs);
            if (cssSelector is null) continue;

            // Verify uniqueness AND node identity across ALL frames!
            if (await VerifyCssSelectorUniqueAcrossFramesAsync(
                    session, tab, frameInputs, cssSelector, item.Frame, item.BackendNodeId, cancellationToken).ConfigureAwait(false))
            {
                verified[FrameNodeKey.For(item.Frame.FrameId, item.BackendNodeId)] = new VerifiedDomLocator(
                    Locator: "loc=css:" + cssSelector,
                    BackendNodeId: item.BackendNodeId,
                    TagName: tagName,
                    InputType: inputType,
                    ExplicitRole: explicitRole);
            }
        }

        return verified.Count > 0 ? verified : null;
    }

    private async Task<bool> VerifyCssSelectorUniqueAcrossFramesAsync(
        SessionState session,
        TabState tab,
        IReadOnlyList<BrowserFrameAxInput> frameInputs,
        string cssSelector,
        BrowserFrameAxInput expectedFrame,
        int expectedBackendNodeId,
        CancellationToken cancellationToken)
    {
        var totalMatches = 0;
        var matchedExpected = false;

        foreach (var frame in frameInputs)
        {
            var doc = await CdpAsync(
                session, tab, "DOM.getDocument", WriteObject(w => w.WriteNumber("depth", 0)),
                TimeSpan.FromSeconds(3), cancellationToken, cdpSessionId: frame.CdpSessionId).ConfigureAwait(false);
            if (doc.Failure is not null
                || !doc.Root.TryGetProperty("root", out var rootNode)
                || !rootNode.TryGetProperty("nodeId", out var rootIdEl)
                || !rootIdEl.TryGetInt32(out var rootNodeId))
            {
                return false;
            }

            var queried = await CdpAsync(
                session,
                tab,
                "DOM.querySelectorAll",
                WriteObject(w =>
                {
                    w.WriteNumber("nodeId", rootNodeId);
                    w.WriteString("selector", cssSelector);
                }),
                TimeSpan.FromSeconds(3),
                cancellationToken,
                cdpSessionId: frame.CdpSessionId).ConfigureAwait(false);
            if (queried.Failure is not null
                || !queried.Root.TryGetProperty("nodeIds", out var nodeIds)
                || nodeIds.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var count = nodeIds.GetArrayLength();
            totalMatches += count;
            if (totalMatches > 1) return false;

            if (count == 1 && string.Equals(frame.FrameId, expectedFrame.FrameId, StringComparison.Ordinal))
            {
                var matchedNodeId = nodeIds[0].GetInt32();
                var desc = await CdpAsync(
                    session,
                    tab,
                    "DOM.describeNode",
                    WriteObject(w => w.WriteNumber("nodeId", matchedNodeId)),
                    TimeSpan.FromSeconds(3),
                    cancellationToken,
                    cdpSessionId: frame.CdpSessionId).ConfigureAwait(false);
                if (desc.Failure is null
                    && desc.Root.TryGetProperty("node", out var dn)
                    && dn.TryGetProperty("backendNodeId", out var bn)
                    && bn.TryGetInt32(out var actualBackendId)
                    && actualBackendId == expectedBackendNodeId)
                {
                    matchedExpected = true;
                }
            }
        }

        return totalMatches == 1 && matchedExpected;
    }

    private static Dictionary<string, string> ParseDomAttributes(JsonElement domNode)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!domNode.TryGetProperty("attributes", out var attrs) || attrs.ValueKind != JsonValueKind.Array) return map;
        var list = attrs.EnumerateArray().ToList();
        for (var i = 0; i + 1 < list.Count; i += 2)
        {
            var key = list[i].ValueKind == JsonValueKind.String ? list[i].GetString() : null;
            var val = list[i + 1].ValueKind == JsonValueKind.String ? list[i + 1].GetString() : null;
            if (!string.IsNullOrEmpty(key) && val is not null) map[key] = val;
        }
        return map;
    }

    private static string? BuildCandidateCssSelector(string? tagName, Dictionary<string, string> attrs)
    {
        var tag = string.IsNullOrWhiteSpace(tagName) ? "*" : tagName.Trim().ToLowerInvariant();
        if (attrs.TryGetValue("data-testid", out var testId) && IsSafeCssAttrValue(testId))
        {
            return $"{tag}[data-testid=\"{EscapeCssString(testId)}\"]";
        }
        if (attrs.TryGetValue("id", out var id) && IsSafeCssIdentifier(id))
        {
            return $"#{id}";
        }
        if (attrs.TryGetValue("name", out var name) && IsSafeCssAttrValue(name))
        {
            return $"{tag}[name=\"{EscapeCssString(name)}\"]";
        }
        return null;
    }

    private static bool IsSafeCssIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80 || char.IsDigit(value[0])) return false;
        foreach (var ch in value)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_')) return false;
        }
        return true;
    }

    private static bool IsSafeCssAttrValue(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 80 && !value.Contains('\n') && !value.Contains('\r');

    private static string EscapeCssString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static bool TryGetBoxBounds(JsonElement root, out double minX, out double minY, out double maxX, out double maxY)
    {
        minX = minY = maxX = maxY = 0;
        if (!root.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.Object) return false;
        if (!model.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return false;
        var xs = new List<double>(4);
        var ys = new List<double>(4);
        var idx = 0;
        foreach (var item in content.EnumerateArray())
        {
            if (!item.TryGetDouble(out var v)) return false;
            if (idx % 2 == 0) xs.Add(v);
            else ys.Add(v);
            idx++;
        }
        if (xs.Count < 4 || ys.Count < 4) return false;
        minX = xs.Min();
        maxX = xs.Max();
        minY = ys.Min();
        maxY = ys.Max();
        return true;
    }

    private static string? AxScalar(JsonElement node, string property)
    {
        if (!node.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty("value", out var val)) return null;
        return val.ValueKind == JsonValueKind.String ? val.GetString() : null;
    }

    // ------------------------------------------------------------------ receipts + common helpers

    private static HashSet<string> SnapshotTabIds(SessionState session) =>
        new(session.Tabs.Keys, StringComparer.Ordinal);

    private BrowserToolResult AttachReceipt(
        SessionState session,
        BrowserToolResult result,
        HashSet<string> beforeTabIds,
        int consoleBaseline)
    {
        var openedTabs = session.Tabs.Values
            .Where(t => !beforeTabIds.Contains(t.Id))
            .Select(t => new BrowserTabInfo
            {
                Id = t.Id,
                Url = t.Url,
                Title = t.Title,
                Active = t.Id == session.ActiveTabId,
            })
            .ToArray();

        var newConsoleErrors = session.ConsoleErrors.Count > consoleBaseline
            ? session.ConsoleErrors.Skip(consoleBaseline).TakeLast(10).ToArray()
            : Array.Empty<string>();

        var pendingDialog = result.PendingDialog ?? session.PendingDialog;
        result.Receipt = new BrowserActionReceipt
        {
            OpenedTabs = openedTabs,
            PendingDialog = pendingDialog,
            ConsoleErrors = newConsoleErrors,
        };
        if (openedTabs.Length > 0) result.OpenedTabs = openedTabs;
        if (pendingDialog is not null) result.PendingDialog = pendingDialog;
        if (newConsoleErrors.Length > 0) result.ConsoleErrors = newConsoleErrors;
        return result;
    }

    private async Task InspectPostActionStateAsync(SessionState session, CancellationToken cancellationToken)
    {
        var stateResp = await _bridge.SendAsync(
            new BrowserBridgeCall("state", session.Id),
            TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);
        if (!stateResp.Ok || stateResp.Data is not { ValueKind: JsonValueKind.Object } data) return;

        RecordConsoleErrors(session, data);
        if (data.TryGetProperty("dialog", out var dialogEl) && dialogEl.ValueKind == JsonValueKind.Object)
        {
            session.PendingDialog = ParseDialogElement(dialogEl);
        }
        else
        {
            session.PendingDialog = null;
        }
        SyncTabs(session, ParseTabs(data), GetString(data, "active_tab"));
    }

    private static void RecordConsoleErrors(SessionState session, JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } obj) return;
        if (!obj.TryGetProperty("console_errors", out var arr) || arr.ValueKind != JsonValueKind.Array) return;

        foreach (var item in arr.EnumerateArray())
        {
            string? text = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Object => GetString(item, "message") ?? GetString(item, "text"),
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (!session.ConsoleErrors.Contains(text, StringComparer.Ordinal))
            {
                session.ConsoleErrors.Add(text);
                if (session.ConsoleErrors.Count > 50)
                {
                    session.ConsoleErrors.RemoveRange(0, session.ConsoleErrors.Count - 50);
                }
            }
        }
    }

    private static BrowserDialogInfo ParseDialogElement(JsonElement dialog) => new()
    {
        Type = GetString(dialog, "type"),
        Message = GetString(dialog, "message"),
        Url = GetString(dialog, "url"),
        DefaultPrompt = GetString(dialog, "defaultPrompt") ?? GetString(dialog, "default_prompt"),
        TabId = GetString(dialog, "tabId") ?? GetString(dialog, "tab_id"),
    };

    private static string ControlName(TaskSpaceControlState state) => state switch
    {
        TaskSpaceControlState.User => "user",
        TaskSpaceControlState.Finished => "finished",
        _ => "agent",
    };

    private static string? NormalizeScope(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "full_page";
        return raw.Trim().ToLowerInvariant() switch
        {
            "full_page" or "full" => "full_page",
            "viewport" or "only_within_viewport" => "viewport",
            "subtree" => "subtree",
            _ => null,
        };
    }

    private static string? StripCssLocatorPrefix(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("loc=css:", StringComparison.OrdinalIgnoreCase)) return trimmed["loc=css:".Length..].Trim();
        if (trimmed.StartsWith("css=", StringComparison.OrdinalIgnoreCase)) return trimmed["css=".Length..].Trim();
        return trimmed;
    }

    private static string? TryFindSiteLearningPath(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        return BrowserSiteLearningStore.TryLoad(url, out var entry) && entry is not null
            ? entry.RelativePath
            : null;
    }

    private static BrowserToolResult? RequireActiveTab(SessionState session, out TabState tab)
    {
        tab = null!;
        if (session.ActiveTabId is null || !session.Tabs.TryGetValue(session.ActiveTabId, out var found))
        {
            return Fail("browser_not_started", "Open the agent browser with browser_navigate first.", permanent: true);
        }
        tab = found;
        return null;
    }

    private static void SyncTabs(SessionState session, IReadOnlyList<BrowserTabInfo> tabs, string? activeTabId)
    {
        var alive = new HashSet<string>(StringComparer.Ordinal);
        foreach (var info in tabs)
        {
            if (string.IsNullOrEmpty(info.Id)) continue;
            alive.Add(info.Id);
            if (!session.Tabs.TryGetValue(info.Id, out var tab))
            {
                tab = new TabState { Id = info.Id };
                session.Tabs[info.Id] = tab;
            }
            if (!string.IsNullOrEmpty(info.Url)) tab.Url = info.Url;
            if (!string.IsNullOrEmpty(info.Title)) tab.Title = info.Title;
        }
        foreach (var id in session.Tabs.Keys.Where(id => !alive.Contains(id)).ToArray())
        {
            session.Tabs.Remove(id);
        }

        var reportedActive = activeTabId ?? tabs.FirstOrDefault(tab => tab.Active)?.Id;
        if (!string.IsNullOrEmpty(reportedActive) && session.Tabs.ContainsKey(reportedActive))
        {
            session.ActiveTabId = reportedActive;
        }
        else if (session.ActiveTabId is not null && !session.Tabs.ContainsKey(session.ActiveTabId))
        {
            session.ActiveTabId = session.Tabs.Keys.FirstOrDefault();
        }
    }

    private static List<BrowserTabInfo> ParseTabs(JsonElement data)
    {
        var list = new List<BrowserTabInfo>();
        if (data.ValueKind == JsonValueKind.Array)
        {
            AddTabs(data, list);
        }
        else if (data.ValueKind == JsonValueKind.Object
                 && data.TryGetProperty("tabs", out var tabs)
                 && tabs.ValueKind == JsonValueKind.Array)
        {
            AddTabs(tabs, list);
        }
        return list;
    }

    private static void AddTabs(JsonElement array, List<BrowserTabInfo> list)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            list.Add(new BrowserTabInfo
            {
                Id = GetString(item, "id"),
                Url = GetString(item, "url"),
                Title = GetString(item, "title"),
                Active = item.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.True,
            });
        }
    }

    private static BrowserNoticeInfo[] CollectNewNotices(SessionState session, JsonElement data)
    {
        var notices = new List<BrowserNoticeInfo>();
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("notices", out var array)
            && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var seq = GetInt64(item, "seq") ?? 0;
                if (seq > 0 && seq <= session.LastNoticeSeq) continue;
                notices.Add(new BrowserNoticeInfo
                {
                    Seq = seq > 0 ? seq : null,
                    Code = GetString(item, "code"),
                    Permission = GetString(item, "permission"),
                    Filename = GetString(item, "filename"),
                    Mode = GetString(item, "mode"),
                    Url = GetString(item, "url"),
                });
            }
        }
        var reportedSeq = GetInt64(data, "notice_seq");
        var maxSeq = notices.Where(notice => notice.Seq is > 0).Select(notice => notice.Seq!.Value).DefaultIfEmpty(0).Max();
        if (reportedSeq is > 0) maxSeq = Math.Max(maxSeq, reportedSeq.Value);
        if (maxSeq > session.LastNoticeSeq) session.LastNoticeSeq = maxSeq;
        return notices.ToArray();
    }

    private static BrowserToolResult? HardStopFailure(SessionState session, IReadOnlyList<BrowserNoticeInfo> notices)
    {
        var notice = notices.FirstOrDefault(item => item.Code is not null && HardStopCodes.Contains(item.Code));
        if (notice is null) return null;
        return Fail(notice.Code!, MessageFor(notice.Code!), permanent: true, notices.ToArray(), spaceId: session.Id);
    }

    private static string MessageFor(string code) => code switch
    {
        "browser_user_in_control" => "Control of this task space is with the user; wait for the user to confirm, then call browser_takeover.",
        "browser_task_space_finished" => "This task space has ended (EGO_TASK_SPACE_INACTIVE) and cannot be resumed with browser_takeover. Wait until the user explicitly asks to continue.",
        "browser_dialog_open" => "The page opened a JavaScript dialog; handle it with browser_dialog before continuing.",
        "browser_permission_denied" => "The page requested a browser permission and it was denied.",
        "browser_download_started" => "The page started a download; use browser_download_save to save downloads inside the workspace.",
        "browser_file_chooser_opened" => "The page opened a file chooser; use browser_upload to set workspace files.",
        "browser_renderer_gone" => "The page renderer exited.",
        _ => "The browser stopped the agent.",
    };

    private static BrowserToolResult FailUserInControl(string spaceId, IReadOnlyList<BrowserNoticeInfo>? notices = null) => new()
    {
        Ok = false,
        Code = "browser_user_in_control",
        Message = MessageFor("browser_user_in_control"),
        Permanent = true,
        SpaceId = spaceId,
        Control = "user",
        Notices = notices is { Count: > 0 } ? notices.ToArray() : null,
    };

    private static BrowserToolResult FailTaskSpaceFinished(string spaceId, IReadOnlyList<BrowserNoticeInfo>? notices = null) => new()
    {
        Ok = false,
        Code = "browser_task_space_finished",
        Message = MessageFor("browser_task_space_finished"),
        Permanent = true,
        SpaceId = spaceId,
        Control = "finished",
        Notices = notices is { Count: > 0 } ? notices.ToArray() : null,
    };

    private static BrowserToolResult FailDialogOpen(
        SessionState session,
        BrowserDialogInfo dialog,
        IReadOnlyList<BrowserNoticeInfo>? notices = null) => new()
    {
        Ok = false,
        Code = "browser_dialog_open",
        Message = !string.IsNullOrEmpty(dialog.Message)
            ? $"The page opened a JavaScript {dialog.Type ?? "modal"} dialog: {dialog.Message}. Call browser_dialog to accept or dismiss it."
            : "The page opened a JavaScript dialog; call browser_dialog to accept or dismiss it.",
        Permanent = true,
        SpaceId = session.Id,
        Control = ControlName(session.Control),
        PendingDialog = dialog,
        Receipt = new BrowserActionReceipt
        {
            OpenedTabs = Array.Empty<BrowserTabInfo>(),
            PendingDialog = dialog,
            ConsoleErrors = Array.Empty<string>(),
        },
        Notices = notices is { Count: > 0 } ? notices.ToArray() : null,
    };

    private static BrowserToolResult WithNotices(BrowserToolResult result, IReadOnlyList<BrowserNoticeInfo> notices)
    {
        if (notices.Count == 0) return result;
        result.Notices = notices.ToArray();
        if (result.Ok)
        {
            var hardStop = notices.FirstOrDefault(item => item.Code is not null && ActionableHardStops.Contains(item.Code));
            if (hardStop is not null)
            {
                result.Ok = false;
                result.Code = hardStop.Code;
                result.Message = MessageFor(hardStop.Code!);
                result.Permanent = true;
            }
        }
        return result;
    }

    private static BrowserNoticeInfo[] MergeNotices(IReadOnlyList<BrowserNoticeInfo> left, IReadOnlyList<BrowserNoticeInfo> right)
    {
        if (left.Count == 0) return right.ToArray();
        if (right.Count == 0) return left.ToArray();
        var result = new List<BrowserNoticeInfo>(left.Count + right.Count);
        result.AddRange(left);
        result.AddRange(right);
        return result.ToArray();
    }

    private static BrowserToolResult FromBridge(SessionState session, BrowserBridgeResponse response)
    {
        if (string.Equals(response.Code, "browser_task_space_finished", StringComparison.Ordinal))
        {
            session.Control = TaskSpaceControlState.Finished;
            return FailTaskSpaceFinished(session.Id);
        }
        if (string.Equals(response.Code, "browser_user_in_control", StringComparison.Ordinal))
        {
            session.Control = TaskSpaceControlState.User;
        }
        if (string.Equals(response.Code, "browser_dialog_open", StringComparison.Ordinal)
            && (response.PendingDialog ?? session.PendingDialog) is { } dialog)
        {
            session.PendingDialog = dialog;
            return FailDialogOpen(session, dialog);
        }

        return new BrowserToolResult
        {
            Ok = false,
            Code = response.Code ?? "browser_bridge_error",
            Message = response.Message,
            Permanent = response.Permanent,
            SpaceId = session.Id,
            PendingDialog = response.PendingDialog,
        };
    }

    private static BrowserToolResult Fail(
        string code,
        string? message,
        bool permanent,
        IReadOnlyList<BrowserNoticeInfo>? notices = null,
        string? spaceId = null) => new()
    {
        Ok = false,
        Code = code,
        Message = message,
        Permanent = permanent,
        SpaceId = spaceId,
        Notices = notices is { Count: > 0 } ? notices.ToArray() : null,
    };

    private static string? NormalizeSessionId(string? sessionId) =>
        BrowserConnectorContext.NormalizeSessionId(sessionId);

    private static JsonElement WriteObject(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static void WriteRemoteString(Utf8JsonWriter writer, string? value)
    {
        writer.WriteStartObject();
        if (value is null) writer.WriteNull("value");
        else writer.WriteString("value", value);
        writer.WriteEndObject();
    }

    private static string? GetObjectId(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) return null;
        if (!result.TryGetProperty("objectId", out var objectId) || objectId.ValueKind != JsonValueKind.String) return null;
        return objectId.GetString();
    }

    private static JsonElement? GetByValue(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) return null;
        if (!result.TryGetProperty("value", out var value)) return null;
        return value.Clone();
    }

    private static string? GetFrameLoaderId(JsonElement root)
    {
        if (!root.TryGetProperty("frameTree", out var tree) || tree.ValueKind != JsonValueKind.Object) return null;
        if (!tree.TryGetProperty("frame", out var frame) || frame.ValueKind != JsonValueKind.Object) return null;
        return GetString(frame, "loaderId");
    }

    private static string? GetFrameId(JsonElement root)
    {
        if (!root.TryGetProperty("frameTree", out var tree) || tree.ValueKind != JsonValueKind.Object) return null;
        if (!tree.TryGetProperty("frame", out var frame) || frame.ValueKind != JsonValueKind.Object) return null;
        return GetString(frame, "id");
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static long? GetInt64(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
    }
}
