using System.Collections.Concurrent;
using System.Text.Json;
using NLog;
using TinadecTools.Runtime;

namespace TinadecTools.Tools.Browser;

/// <summary>
/// Session and tab state for the agent browser, plus the tool-level semantics: snapshot,
/// navigation, waiting and screenshots. The Claude/ego-lite contract lives here: refs belong to a
/// tab, a document change invalidates them, and every expected failure is a stable code rather
/// than a thrown exception.
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
        "browser_dialog_open",
        "browser_permission_denied",
        "browser_download_started",
        "browser_file_chooser_opened",
        "browser_renderer_gone",
    };

    private static readonly HashSet<string> ActionableHardStops = new(StringComparer.Ordinal)
    {
        "browser_permission_denied",
        "browser_download_started",
        "browser_file_chooser_opened",
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
        "async function(text,urlPart,timeoutMs){" +
        "const deadline=Date.now()+timeoutMs;" +
        "while(Date.now()<deadline){" +
        "if(text){const body=document.body?document.body.innerText:'';if(body&&body.indexOf(text)>=0)return {ok:true,reason:'text'};}" +
        "if(urlPart&&location.href.indexOf(urlPart)>=0)return {ok:true,reason:'url'};" +
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

    private BrowserNavigationPolicy CurrentNavigationPolicy()
    {
        var allowPrivate = ToolExecutionContext.Current?.Boolean("browser", "allow_private_hosts", false) ?? false;
        return allowPrivate ? new BrowserNavigationPolicy(allowPrivate: true) : _navigationPolicy;
    }

    // ------------------------------------------------------------------ tools

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
        var refresh = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
        if (refresh.Failure is not null) return refresh.Failure;

        var missing = RequireActiveTab(session, out var tab);
        var notices = refresh.Notices;
        if (missing is not null) return WithNotices(missing, notices);

        var frame = await CdpAsync(session, tab, "Page.getFrameTree", EmptyObject, ActionTimeout, cancellationToken).ConfigureAwait(false);
        if (frame.Failure is not null) return WithNotices(frame.Failure, MergeNotices(notices, frame.Notices));
        notices = MergeNotices(notices, frame.Notices);
        var loaderId = GetFrameLoaderId(frame.Root);
        var frameId = GetFrameId(frame.Root);
        if (!string.IsNullOrEmpty(loaderId) && !string.Equals(loaderId, tab.LoaderId, StringComparison.Ordinal))
        {
            tab.Refs.Clear();
            tab.NextRef = 1;
            tab.LoaderId = loaderId;
            tab.FrameId = frameId;
        }
        else if (tab.LoaderId is null)
        {
            tab.LoaderId = loaderId;
            tab.FrameId = frameId;
        }

        var ax = await CdpAsync(
            session, tab, "Accessibility.getFullAXTree", EmptyObject, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (ax.Failure is not null) return WithNotices(ax.Failure, MergeNotices(notices, ax.Notices));
        notices = MergeNotices(notices, ax.Notices);

        var built = BrowserSnapshotBuilder.Build(
            ax.Root,
            tab.Refs,
            tab.NextRef,
            args.MaxChars
                ?? ToolExecutionContext.Current?.Integer(
                    "browser", "max_snapshot_chars", BrowserSnapshotBuilder.DefaultMaxChars,
                    BrowserSnapshotBuilder.MinimumMaxChars, BrowserSnapshotBuilder.MaximumMaxChars)
                ?? BrowserSnapshotBuilder.DefaultMaxChars);
        tab.Refs = built.Refs;
        tab.NextRef = built.NextRef;

        var result = new BrowserToolResult
        {
            Ok = true,
            TabId = tab.Id,
            Url = tab.Url,
            Title = tab.Title,
            Snapshot = built.Text,
            Truncated = built.Truncated,
            RefCount = built.RefCount,
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
            var refresh = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
            if (refresh.Failure is not null) return refresh.Failure;

            var notices = refresh.Notices;
            if (session.ActiveTabId is null || !session.Tabs.ContainsKey(session.ActiveTabId))
            {
                var opened = await _bridge.SendAsync(
                    new BrowserBridgeCall("tabs/open", session.Id, Url: url.ToString()),
                    TimeSpan.FromSeconds(30),
                    cancellationToken).ConfigureAwait(false);
                if (!opened.Ok) return FromBridge(opened);
                notices = MergeNotices(notices, CollectNewNotices(session, opened.Data ?? EmptyObject));

                var afterOpen = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
                if (afterOpen.Failure is not null) return afterOpen.Failure;
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
                if (navigate.Failure is not null) return WithNotices(navigate.Failure, MergeNotices(notices, navigate.Notices));
                notices = MergeNotices(notices, navigate.Notices);
            }

            var missing = RequireActiveTab(session, out var tab);
            if (missing is not null) return WithNotices(missing, notices);

            var waitMs = Math.Clamp(args.WaitMs ?? 10_000, 0, 30_000);
            var ready = true;
            if (waitMs > 0)
            {
                var readyResult = await WaitForReadyAsync(session, tab, waitMs, cancellationToken).ConfigureAwait(false);
                if (readyResult.Failure is not null) return WithNotices(readyResult.Failure, MergeNotices(notices, readyResult.Notices));
                notices = MergeNotices(notices, readyResult.Notices);
                ready = readyResult.Ready;
            }

            // Refresh once more so the caller sees the title/final URL and any notice that landed
            // while the document was loading.
            var finalRefresh = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
            if (finalRefresh.Failure is not null) return finalRefresh.Failure;
            notices = MergeNotices(notices, finalRefresh.Notices);

            var result = new BrowserToolResult
            {
                Ok = true,
                TabId = tab.Id,
                Url = tab.Url,
                Title = tab.Title,
                Ready = ready,
            };
            return WithNotices(result, notices);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> TabsListAsync(string? sessionId, CancellationToken cancellationToken)
    {
        var id = NormalizeSessionId(sessionId);
        if (id is null) return Fail("browser_session_invalid", "A session id is required.", permanent: true);
        var session = GetSession(id);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await _bridge.SendAsync(
                new BrowserBridgeCall("tabs/list", session.Id),
                ActionTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!response.Ok) return FromBridge(response);

            var tabs = ParseTabs(response.Data ?? EmptyObject);
            SyncTabs(session, tabs, activeTabId: null);
            return new BrowserToolResult
            {
                Ok = true,
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
        var text = string.IsNullOrWhiteSpace(args.Text) ? null : args.Text;
        var urlPart = string.IsNullOrWhiteSpace(args.UrlContains) ? null : args.UrlContains;
        if (text is null && urlPart is null)
        {
            return Fail("browser_wait_invalid", "Provide text or url_contains to wait for.", permanent: true);
        }

        var timeoutMs = Math.Clamp(
            args.TimeoutMs
                ?? ToolExecutionContext.Current?.Integer("browser", "wait_timeout_ms", 15_000, 100, 60_000)
                ?? 15_000,
            100,
            60_000);
        var session = GetSession(id);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
            var result = ok
                ? new BrowserToolResult { Ok = true, TabId = tab.Id, Reason = reason }
                : Fail("browser_wait_timeout", $"The condition did not appear within {timeoutMs} ms.", permanent: false);
            return WithNotices(result, MergeNotices(refresh.Notices, call.Notices));
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> ScreenshotAsync(string? sessionId, BrowserScreenshotArgs args, CancellationToken cancellationToken)
    {
        var id = NormalizeSessionId(sessionId);
        if (id is null) return Fail("browser_session_invalid", "A session id is required.", permanent: true);
        var session = GetSession(id);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var refresh = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
            if (refresh.Failure is not null) return refresh.Failure;
            var missing = RequireActiveTab(session, out var tab);
            if (missing is not null) return WithNotices(missing, refresh.Notices);

            var response = await _bridge.SendAsync(
                new BrowserBridgeCall("screenshot", session.Id, tab.Id, FullPage: args.FullPage == true),
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);
            if (!response.Ok) return FromBridge(response);

            var data = response.Data ?? EmptyObject;
            var path = GetString(data, "path");
            var result = new BrowserToolResult
            {
                Ok = true,
                TabId = tab.Id,
                Url = tab.Url,
                Title = tab.Title,
                Path = path,
                Bytes = GetInt64(data, "bytes"),
            };
            // The context copy the bridge downscaled for model context (or the full file when the
            // page was already small / the bridge predates the field). Core loads image.path into
            // the conversation; a missing bridge field degrades to the full capture.
            if (path is not null)
            {
                result.Image = new BrowserImageInfo
                {
                    Path = GetString(data, "context_path") ?? path,
                    MediaType = "image/png",
                    Width = (int?)GetInt64(data, "context_width"),
                    Height = (int?)GetInt64(data, "context_height"),
                    Bytes = GetInt64(data, "context_bytes") ?? result.Bytes,
                };
            }
            var notices = MergeNotices(refresh.Notices, CollectNewNotices(session, data));
            return WithNotices(result, notices);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    // ------------------------------------------------------------------ state

    private sealed class SessionState
    {
        public string Id { get; init; } = string.Empty;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Dictionary<string, TabState> Tabs { get; } = new(StringComparer.Ordinal);
        public string? ActiveTabId { get; set; }
        public long LastNoticeSeq { get; set; }
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

    private readonly record struct RefreshResult(BrowserToolResult? Failure, BrowserNoticeInfo[] Notices);

    private readonly record struct CdpResult(JsonElement Root, BrowserToolResult? Failure, BrowserNoticeInfo[] Notices);

    private readonly record struct ObjectIdResult(string? ObjectId, BrowserToolResult? Failure, BrowserNoticeInfo[] Notices);

    private readonly record struct ReadyResult(bool Ready, BrowserToolResult? Failure, BrowserNoticeInfo[] Notices);

    private SessionState GetSession(string sessionId) =>
        _sessions.GetOrAdd(sessionId, key => new SessionState { Id = key });

    private async Task<RefreshResult> RefreshAsync(SessionState session, CancellationToken cancellationToken)
    {
        var response = await _bridge.SendAsync(
            new BrowserBridgeCall("state", session.Id),
            ActionTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!response.Ok) return new RefreshResult(FromBridge(response), Array.Empty<BrowserNoticeInfo>());

        var data = response.Data ?? EmptyObject;
        var notices = CollectNewNotices(session, data);
        var control = GetString(data, "control");
        if (string.Equals(control, "user", StringComparison.Ordinal))
        {
            return new RefreshResult(
                Fail("browser_user_in_control", "The user has taken control of the browser; stop and hand back.", permanent: true, notices: notices),
                notices);
        }
        if (data.TryGetProperty("dialog", out var dialog) && dialog.ValueKind == JsonValueKind.Object)
        {
            var message = GetString(dialog, "message");
            return new RefreshResult(
                Fail("browser_dialog_open",
                    message is { Length: > 0 }
                        ? $"The page opened a JavaScript dialog: {message}"
                        : "The page opened a JavaScript dialog; the agent is paused.",
                    permanent: true,
                    notices: notices),
                notices);
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
        CancellationToken cancellationToken)
    {
        Logger.Debug("browser cdp {Method}", method);
        var response = await _bridge.SendAsync(
            new BrowserBridgeCall("cdp", session.Id, tab.Id, method, parameters),
            timeout,
            cancellationToken).ConfigureAwait(false);
        if (!response.Ok) return new CdpResult(EmptyObject, FromBridge(response), Array.Empty<BrowserNoticeInfo>());

        var data = response.Data ?? EmptyObject;
        var notices = CollectNewNotices(session, data);
        var failure = HardStopFailure(notices);
        var root = data.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
            ? result.Clone()
            : EmptyObject;
        return new CdpResult(root, failure, notices);
    }

    private async Task<ObjectIdResult> GetWindowObjectIdAsync(SessionState session, TabState tab, CancellationToken cancellationToken)
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
            cancellationToken).ConfigureAwait(false);
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

    // ------------------------------------------------------------------ helpers

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

    private static BrowserToolResult? HardStopFailure(IReadOnlyList<BrowserNoticeInfo> notices)
    {
        var notice = notices.FirstOrDefault(item => item.Code is not null && HardStopCodes.Contains(item.Code));
        if (notice is null) return null;
        return Fail(notice.Code!, MessageFor(notice.Code!), permanent: true, notices.ToArray());
    }

    private static string MessageFor(string code) => code switch
    {
        "browser_user_in_control" => "The user has taken control of the browser; stop and hand back.",
        "browser_dialog_open" => "The page opened a JavaScript dialog; the agent is paused.",
        "browser_permission_denied" => "The page requested a browser permission and it was denied.",
        "browser_download_started" => "The page started a download; downloads are not supported.",
        "browser_file_chooser_opened" => "The page opened a file chooser; uploads are not supported.",
        "browser_renderer_gone" => "The page renderer exited.",
        _ => "The browser stopped the agent.",
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

    private static BrowserToolResult FromBridge(BrowserBridgeResponse response) => new()
    {
        Ok = false,
        Code = response.Code ?? "browser_bridge_error",
        Message = response.Message,
        Permanent = response.Permanent,
    };

    private static BrowserToolResult Fail(string code, string? message, bool permanent, IReadOnlyList<BrowserNoticeInfo>? notices = null) => new()
    {
        Ok = false,
        Code = code,
        Message = message,
        Permanent = permanent,
        Notices = notices is { Count: > 0 } ? notices.ToArray() : null,
    };

    private static string? NormalizeSessionId(string? sessionId)
    {
        var value = sessionId?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 200) return null;
        foreach (var character in value)
        {
            if (!char.IsLetterOrDigit(character) && character is not ('.' or '_' or ':' or '-')) return null;
        }
        return value;
    }

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
