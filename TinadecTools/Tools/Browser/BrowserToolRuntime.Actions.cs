using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TinadecTools.Tools.Browser;

/// <summary>
/// Action half of the browser runtime: ref → element → real input. The rules are the ego-lite
/// ones that matter: resolve the ref against the current document, scroll, hit-test through open
/// shadow roots, dispatch a real event, and never replay an input that was already dispatched.
/// </summary>
internal sealed partial class BrowserToolRuntime
{
    private const string HitTestFunction =
        "function(){" +
        "const el=this;" +
        "if(!el||!el.isConnected)return {ok:false,reason:'detached'};" +
        "if(el.disabled)return {ok:false,reason:'disabled'};" +
        "const r=el.getBoundingClientRect();" +
        "if(r.width<1||r.height<1)return {ok:false,reason:'zero-size'};" +
        "const s=getComputedStyle(el);" +
        "if(s.display==='none'||s.visibility==='hidden'||s.pointerEvents==='none')return {ok:false,reason:'hidden'};" +
        "const x=r.left+r.width/2;const y=r.top+r.height/2;" +
        "const root=el.getRootNode?el.getRootNode():document;" +
        "const hit=root&&root.elementFromPoint?root.elementFromPoint(x,y):null;" +
        "let node=hit;" +
        "while(node&&node!==el){" +
        "if(node.assignedSlot)node=node.assignedSlot;" +
        "else if(node.parentNode)node=node.parentNode;" +
        "else if(node.host)node=node.host;" +
        "else node=null;" +
        "}" +
        "if(node!==el)return {ok:false,reason:'obscured'};" +
        "return {ok:true};" +
        "}";

    private const string SelectFunction =
        "function(){if(this.select)this.select();else if(this.setSelectionRange)this.setSelectionRange(0,(this.value||'').length);return true;}";

    private const string ReadValueFunction =
        "function(){return this.value!==undefined?this.value:(this.textContent||'');}";

    private const string SetValueFunction =
        "function(value){" +
        "const proto=this instanceof HTMLTextAreaElement?HTMLTextAreaElement.prototype:HTMLInputElement.prototype;" +
        "const descriptor=Object.getOwnPropertyDescriptor(proto,'value');" +
        "if(descriptor&&descriptor.set)descriptor.set.call(this,value);else this.value=value;" +
        "this.dispatchEvent(new Event('input',{bubbles:true}));" +
        "this.dispatchEvent(new Event('change',{bubbles:true}));" +
        "return this.value;" +
        "}";

    private static readonly HashSet<string> BatchTools = new(StringComparer.Ordinal)
    {
        "browser_click",
        "browser_type",
        "browser_press",
        "browser_scroll",
    };

    private readonly record struct TargetResult(
        BrowserRef? Ref,
        double X,
        double Y,
        string? ObjectId,
        BrowserToolResult? Failure,
        BrowserNoticeInfo[] Notices);

    private readonly record struct KeyStroke(string Key, string Code, int VirtualKey, string? Text, int Modifiers);

    // ------------------------------------------------------------------ click

    public async Task<BrowserToolResult> ClickAsync(string? sessionId, BrowserClickArgs args, CancellationToken cancellationToken)
    {
        var id = NormalizeSessionId(sessionId);
        if (id is null) return Fail("browser_session_invalid", "A session id is required.", permanent: true);
        var session = GetSession(id);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ClickLockedAsync(session, args, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async Task<BrowserToolResult> ClickLockedAsync(SessionState session, BrowserClickArgs args, CancellationToken cancellationToken)
    {
        if (args.Ref is not { } refId || refId <= 0)
        {
            return Fail("browser_ref_required", "A ref from browser_snapshot is required.", permanent: true);
        }
        var button = (args.Button ?? "left").Trim().ToLowerInvariant();
        if (button is not ("left" or "right" or "middle"))
        {
            return Fail("browser_click_invalid", "button must be left, right, or middle.", permanent: true);
        }
        var clickCount = Math.Clamp(args.ClickCount ?? 1, 1, 2);

        var refresh = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
        if (refresh.Failure is not null) return refresh.Failure;
        var missing = RequireActiveTab(session, out var tab);
        if (missing is not null) return WithNotices(missing, refresh.Notices);

        var target = await PrepareTargetAsync(session, tab, refId, cancellationToken).ConfigureAwait(false);
        if (target.Failure is not null) return WithNotices(target.Failure, target.Notices);

        var dispatch = await DispatchMouseAsync(session, tab, target.X, target.Y, button, clickCount, cancellationToken).ConfigureAwait(false);
        var notices = MergeNotices(MergeNotices(refresh.Notices, target.Notices), dispatch.Notices);
        if (dispatch.Failure is not null) return WithNotices(dispatch.Failure, notices);

        return WithNotices(new BrowserToolResult
        {
            Ok = true,
            TabId = tab.Id,
            Url = tab.Url,
            Title = tab.Title,
            Ref = refId,
        }, notices);
    }

    // ------------------------------------------------------------------ type

    public async Task<BrowserToolResult> TypeAsync(string? sessionId, BrowserTypeArgs args, CancellationToken cancellationToken)
    {
        var id = NormalizeSessionId(sessionId);
        if (id is null) return Fail("browser_session_invalid", "A session id is required.", permanent: true);
        var session = GetSession(id);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await TypeLockedAsync(session, args, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async Task<BrowserToolResult> TypeLockedAsync(SessionState session, BrowserTypeArgs args, CancellationToken cancellationToken)
    {
        if (args.Ref is not { } refId || refId <= 0)
        {
            return Fail("browser_ref_required", "A ref from browser_snapshot is required.", permanent: true);
        }
        var text = args.Text ?? string.Empty;
        if (text.Length > 32_000)
        {
            return Fail("browser_type_invalid", "Text is longer than 32000 characters.", permanent: true);
        }

        var refresh = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
        if (refresh.Failure is not null) return refresh.Failure;
        var missing = RequireActiveTab(session, out var tab);
        if (missing is not null) return WithNotices(missing, refresh.Notices);

        var target = await PrepareTargetAsync(session, tab, refId, cancellationToken).ConfigureAwait(false);
        if (target.Failure is not null) return WithNotices(target.Failure, target.Notices);
        var notices = MergeNotices(refresh.Notices, target.Notices);

        var focus = await CdpAsync(session, tab, "DOM.focus",
            WriteObject(writer => writer.WriteNumber("backendNodeId", target.Ref!.BackendNodeId)),
            ActionTimeout, cancellationToken).ConfigureAwait(false);
        if (focus.Failure is not null) return WithNotices(focus.Failure, MergeNotices(notices, focus.Notices));
        notices = MergeNotices(notices, focus.Notices);

        var selected = await CallFunctionValueAsync(session, tab, target.ObjectId!, SelectFunction, null, cancellationToken).ConfigureAwait(false);
        if (selected.Failure is not null) return WithNotices(selected.Failure, MergeNotices(notices, selected.Notices));
        notices = MergeNotices(notices, selected.Notices);

        var insert = await CdpAsync(session, tab, "Input.insertText",
            WriteObject(writer => writer.WriteString("text", text)),
            ActionTimeout, cancellationToken).ConfigureAwait(false);
        if (insert.Failure is not null) return WithNotices(insert.Failure, MergeNotices(notices, insert.Notices));
        notices = MergeNotices(notices, insert.Notices);

        var actual = await ReadValueAsync(session, tab, target.ObjectId!, cancellationToken).ConfigureAwait(false);
        if (actual.Value is not null || actual.Failure is null)
        {
            notices = MergeNotices(notices, actual.Notices);
        }
        if (actual.Failure is not null) return WithNotices(actual.Failure, notices);

        if (!string.Equals(actual.Value, text, StringComparison.Ordinal))
        {
            var fallback = await CallFunctionValueAsync(
                session, tab, target.ObjectId!, SetValueFunction, JsonString(text), cancellationToken).ConfigureAwait(false);
            notices = MergeNotices(notices, fallback.Notices);
            if (fallback.Failure is not null) return WithNotices(fallback.Failure, notices);
            var reread = await ReadValueAsync(session, tab, target.ObjectId!, cancellationToken).ConfigureAwait(false);
            notices = MergeNotices(notices, reread.Notices);
            if (reread.Failure is not null) return WithNotices(reread.Failure, notices);
            actual = reread;
        }

        if (args.Submit == true)
        {
            var submit = await DispatchKeyAsync(session, tab, ParseKey("Enter")!.Value, cancellationToken).ConfigureAwait(false);
            notices = MergeNotices(notices, submit.Notices);
            if (submit.Failure is not null) return WithNotices(submit.Failure, notices);
        }

        return WithNotices(new BrowserToolResult
        {
            Ok = true,
            TabId = tab.Id,
            Url = tab.Url,
            Title = tab.Title,
            Ref = refId,
            Value = actual.Value,
            Verified = string.Equals(actual.Value, text, StringComparison.Ordinal),
        }, notices);
    }

    // ------------------------------------------------------------------ press

    public async Task<BrowserToolResult> PressAsync(string? sessionId, BrowserPressArgs args, CancellationToken cancellationToken)
    {
        var id = NormalizeSessionId(sessionId);
        if (id is null) return Fail("browser_session_invalid", "A session id is required.", permanent: true);
        var session = GetSession(id);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await PressLockedAsync(session, args, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async Task<BrowserToolResult> PressLockedAsync(SessionState session, BrowserPressArgs args, CancellationToken cancellationToken)
    {
        var stroke = ParseKey(args.Key);
        if (stroke is null)
        {
            return Fail("browser_key_unsupported", "Use a named key (Enter, Tab, Escape, arrows) or a single character, optionally with Ctrl/Shift/Alt/Meta.", permanent: true);
        }

        var refresh = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
        if (refresh.Failure is not null) return refresh.Failure;
        var missing = RequireActiveTab(session, out var tab);
        if (missing is not null) return WithNotices(missing, refresh.Notices);
        var notices = refresh.Notices;

        if (args.Ref is int refId && refId > 0)
        {
            var target = await PrepareTargetAsync(session, tab, refId, cancellationToken).ConfigureAwait(false);
            if (target.Failure is not null) return WithNotices(target.Failure, target.Notices);
            notices = MergeNotices(notices, target.Notices);
            var focus = await CdpAsync(session, tab, "DOM.focus",
                WriteObject(writer => writer.WriteNumber("backendNodeId", target.Ref!.BackendNodeId)),
                ActionTimeout, cancellationToken).ConfigureAwait(false);
            if (focus.Failure is not null) return WithNotices(focus.Failure, MergeNotices(notices, focus.Notices));
            notices = MergeNotices(notices, focus.Notices);
        }

        var dispatch = await DispatchKeyAsync(session, tab, stroke.Value, cancellationToken).ConfigureAwait(false);
        notices = MergeNotices(notices, dispatch.Notices);
        if (dispatch.Failure is not null) return WithNotices(dispatch.Failure, notices);

        return WithNotices(new BrowserToolResult
        {
            Ok = true,
            TabId = tab.Id,
            Url = tab.Url,
            Title = tab.Title,
            Ref = args.Ref,
        }, notices);
    }

    // ------------------------------------------------------------------ scroll

    public async Task<BrowserToolResult> ScrollAsync(string? sessionId, BrowserScrollArgs args, CancellationToken cancellationToken)
    {
        var id = NormalizeSessionId(sessionId);
        if (id is null) return Fail("browser_session_invalid", "A session id is required.", permanent: true);
        var session = GetSession(id);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ScrollLockedAsync(session, args, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async Task<BrowserToolResult> ScrollLockedAsync(SessionState session, BrowserScrollArgs args, CancellationToken cancellationToken)
    {
        var deltaX = Math.Clamp(args.DeltaX ?? 0, -2000, 2000);
        var deltaY = Math.Clamp(args.DeltaY ?? 0, -2000, 2000);
        if (deltaX == 0 && deltaY == 0)
        {
            return Fail("browser_scroll_invalid", "Provide delta_x or delta_y.", permanent: true);
        }

        var refresh = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
        if (refresh.Failure is not null) return refresh.Failure;
        var missing = RequireActiveTab(session, out var tab);
        if (missing is not null) return WithNotices(missing, refresh.Notices);
        var notices = refresh.Notices;

        double x;
        double y;
        if (args.Ref is int refId && refId > 0)
        {
            var target = await PrepareTargetAsync(session, tab, refId, cancellationToken).ConfigureAwait(false);
            if (target.Failure is not null) return WithNotices(target.Failure, target.Notices);
            notices = MergeNotices(notices, target.Notices);
            x = target.X;
            y = target.Y;
        }
        else
        {
            var metrics = await CdpAsync(session, tab, "Runtime.evaluate",
                WriteObject(writer =>
                {
                    writer.WriteString("expression", "({w:innerWidth,h:innerHeight})");
                    writer.WriteBoolean("returnByValue", true);
                }),
                ActionTimeout, cancellationToken).ConfigureAwait(false);
            if (metrics.Failure is not null) return WithNotices(metrics.Failure, MergeNotices(notices, metrics.Notices));
            notices = MergeNotices(notices, metrics.Notices);
            var value = GetByValue(metrics.Root);
            var width = value is { ValueKind: JsonValueKind.Object } v && v.TryGetProperty("w", out var w) && w.TryGetDouble(out var wd) ? wd : 800;
            var height = value is { ValueKind: JsonValueKind.Object } v2 && v2.TryGetProperty("h", out var h) && h.TryGetDouble(out var hd) ? hd : 600;
            x = width / 2;
            y = height / 2;
        }

        var wheel = await CdpAsync(session, tab, "Input.dispatchMouseEvent",
            WriteObject(writer =>
            {
                writer.WriteString("type", "mouseWheel");
                writer.WriteNumber("x", x);
                writer.WriteNumber("y", y);
                writer.WriteNumber("deltaX", deltaX);
                writer.WriteNumber("deltaY", deltaY);
            }),
            ActionTimeout, cancellationToken).ConfigureAwait(false);
        notices = MergeNotices(notices, wheel.Notices);
        if (wheel.Failure is not null) return WithNotices(wheel.Failure, notices);

        return WithNotices(new BrowserToolResult
        {
            Ok = true,
            TabId = tab.Id,
            Url = tab.Url,
            Title = tab.Title,
            Ref = args.Ref,
        }, notices);
    }

    // ------------------------------------------------------------------ history

    public async Task<BrowserToolResult> HistoryAsync(string? sessionId, BrowserHistoryArgs args, CancellationToken cancellationToken)
    {
        var id = NormalizeSessionId(sessionId);
        if (id is null) return Fail("browser_session_invalid", "A session id is required.", permanent: true);
        var action = (args.Action ?? string.Empty).Trim().ToLowerInvariant();
        if (action is not ("back" or "forward" or "reload"))
        {
            return Fail("browser_history_invalid", "action must be back, forward, or reload.", permanent: true);
        }

        var session = GetSession(id);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var refresh = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
            if (refresh.Failure is not null) return refresh.Failure;
            var missing = RequireActiveTab(session, out var tab);
            if (missing is not null) return WithNotices(missing, refresh.Notices);
            var notices = refresh.Notices;

            CdpResult navigation;
            if (action == "reload")
            {
                navigation = await CdpAsync(session, tab, "Page.reload", EmptyObject,
                    TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var history = await CdpAsync(session, tab, "Page.getNavigationHistory", EmptyObject,
                    ActionTimeout, cancellationToken).ConfigureAwait(false);
                if (history.Failure is not null) return WithNotices(history.Failure, MergeNotices(notices, history.Notices));
                notices = MergeNotices(notices, history.Notices);
                var entries = history.Root.TryGetProperty("entries", out var entriesElement) && entriesElement.ValueKind == JsonValueKind.Array
                    ? entriesElement
                    : default;
                var current = history.Root.TryGetProperty("currentIndex", out var currentElement) && currentElement.TryGetInt32(out var currentIndex)
                    ? currentIndex
                    : -1;
                var targetIndex = action == "back" ? current - 1 : current + 1;
                if (entries.ValueKind != JsonValueKind.Array || targetIndex < 0 || targetIndex >= entries.GetArrayLength())
                {
                    return WithNotices(Fail("browser_history_unavailable", $"There is no {action} entry.", permanent: false), notices);
                }
                var entry = entries.EnumerateArray().ElementAt(targetIndex);
                var entryId = entry.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var entryNumber)
                    ? entryNumber
                    : -1;
                if (entryId < 0)
                {
                    return WithNotices(Fail("browser_history_unavailable", "The history entry has no id.", permanent: false), notices);
                }
                navigation = await CdpAsync(session, tab, "Page.navigateToHistoryEntry",
                    WriteObject(writer => writer.WriteNumber("entryId", entryId)),
                    TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            }

            notices = MergeNotices(notices, navigation.Notices);
            if (navigation.Failure is not null) return WithNotices(navigation.Failure, notices);
            tab.Refs.Clear();
            tab.NextRef = 1;
            tab.LoaderId = null;

            return WithNotices(new BrowserToolResult
            {
                Ok = true,
                TabId = tab.Id,
                Url = tab.Url,
                Title = tab.Title,
            }, notices);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    // ------------------------------------------------------------------ tabs

    public async Task<BrowserToolResult> TabOpenAsync(string? sessionId, BrowserTabOpenArgs args, CancellationToken cancellationToken)
    {
        var id = NormalizeSessionId(sessionId);
        if (id is null) return Fail("browser_session_invalid", "A session id is required.", permanent: true);
        Uri? url = null;
        if (!string.IsNullOrWhiteSpace(args.Url))
        {
            if (!CurrentNavigationPolicy().TryNormalize(args.Url, out var parsed, out var error))
            {
                return Fail("browser_navigation_blocked", error, permanent: true);
            }
            url = parsed;
        }

        var session = GetSession(id);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var refresh = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
            if (refresh.Failure is not null) return refresh.Failure;
            var opened = await _bridge.SendAsync(
                new BrowserBridgeCall("tabs/open", session.Id, Url: url?.ToString()),
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);
            if (!opened.Ok) return FromBridge(opened);
            var notices = MergeNotices(refresh.Notices, CollectNewNotices(session, opened.Data ?? EmptyObject));
            var after = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
            if (after.Failure is not null) return WithNotices(after.Failure, MergeNotices(notices, after.Notices));
            notices = MergeNotices(notices, after.Notices);
            return WithNotices(new BrowserToolResult
            {
                Ok = true,
                TabId = session.ActiveTabId,
                Tabs = TabInfos(session),
            }, notices);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> TabSelectAsync(string? sessionId, BrowserTabArgs args, CancellationToken cancellationToken)
    {
        var id = NormalizeSessionId(sessionId);
        if (id is null) return Fail("browser_session_invalid", "A session id is required.", permanent: true);
        var tabId = NormalizeTabId(args.TabId);
        if (tabId is null) return Fail("browser_tab_invalid", "A tab id is required.", permanent: true);

        var session = GetSession(id);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var refresh = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
            if (refresh.Failure is not null) return refresh.Failure;
            var selected = await _bridge.SendAsync(
                new BrowserBridgeCall("tabs/select", session.Id, TabId: tabId),
                ActionTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!selected.Ok) return FromBridge(selected);
            var notices = MergeNotices(refresh.Notices, CollectNewNotices(session, selected.Data ?? EmptyObject));
            var after = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
            if (after.Failure is not null) return WithNotices(after.Failure, MergeNotices(notices, after.Notices));
            return WithNotices(new BrowserToolResult
            {
                Ok = true,
                TabId = session.ActiveTabId,
                Tabs = TabInfos(session),
            }, MergeNotices(notices, after.Notices));
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> TabCloseAsync(string? sessionId, BrowserTabArgs args, CancellationToken cancellationToken)
    {
        var id = NormalizeSessionId(sessionId);
        if (id is null) return Fail("browser_session_invalid", "A session id is required.", permanent: true);
        var tabId = NormalizeTabId(args.TabId);
        if (tabId is null) return Fail("browser_tab_invalid", "A tab id is required.", permanent: true);

        var session = GetSession(id);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var refresh = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
            if (refresh.Failure is not null) return refresh.Failure;
            var closed = await _bridge.SendAsync(
                new BrowserBridgeCall("tabs/close", session.Id, TabId: tabId),
                ActionTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!closed.Ok) return FromBridge(closed);
            var notices = MergeNotices(refresh.Notices, CollectNewNotices(session, closed.Data ?? EmptyObject));
            var after = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
            if (after.Failure is not null) return WithNotices(after.Failure, MergeNotices(notices, after.Notices));
            return WithNotices(new BrowserToolResult
            {
                Ok = true,
                TabId = session.ActiveTabId,
                Tabs = TabInfos(session),
            }, MergeNotices(notices, after.Notices));
        }
        finally
        {
            session.Gate.Release();
        }
    }

    // ------------------------------------------------------------------ batch

    public async Task<BrowserToolResult> BatchAsync(string? sessionId, BrowserBatchArgs args, CancellationToken cancellationToken)
    {
        var id = NormalizeSessionId(sessionId);
        if (id is null) return Fail("browser_session_invalid", "A session id is required.", permanent: true);
        if (args.Steps is not { Length: > 0 and <= 20 })
        {
            return Fail("browser_batch_invalid", "steps must contain between 1 and 20 actions.", permanent: true);
        }
        foreach (var step in args.Steps)
        {
            if (step.Tool is null || !BatchTools.Contains(step.Tool))
            {
                return Fail("browser_batch_invalid",
                    "Each step must be browser_click, browser_type, browser_press, or browser_scroll.", permanent: true);
            }
        }

        var session = GetSession(id);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var refresh = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
            if (refresh.Failure is not null) return refresh.Failure;
            var missing = RequireActiveTab(session, out _);
            if (missing is not null) return WithNotices(missing, refresh.Notices);
            var notices = refresh.Notices;
            var results = new List<BrowserBatchStepResult>(args.Steps.Length);
            BrowserToolResult? failure = null;

            for (var index = 0; index < args.Steps.Length; index++)
            {
                var step = args.Steps[index];
                var stepResult = step.Tool switch
                {
                    "browser_click" => await ClickLockedAsync(session, StepArgs(step.Args, BrowserJsonContext.Default.BrowserClickArgs), cancellationToken).ConfigureAwait(false),
                    "browser_type" => await TypeLockedAsync(session, StepArgs(step.Args, BrowserJsonContext.Default.BrowserTypeArgs), cancellationToken).ConfigureAwait(false),
                    "browser_press" => await PressLockedAsync(session, StepArgs(step.Args, BrowserJsonContext.Default.BrowserPressArgs), cancellationToken).ConfigureAwait(false),
                    "browser_scroll" => await ScrollLockedAsync(session, StepArgs(step.Args, BrowserJsonContext.Default.BrowserScrollArgs), cancellationToken).ConfigureAwait(false),
                    _ => Fail("browser_batch_invalid", "Unknown step.", permanent: true),
                };
                results.Add(new BrowserBatchStepResult
                {
                    Index = index,
                    Tool = step.Tool,
                    Ok = stepResult.Ok,
                    Code = stepResult.Code,
                    Message = stepResult.Message,
                });
                notices = MergeNotices(notices, stepResult.Notices ?? Array.Empty<BrowserNoticeInfo>());
                if (!stepResult.Ok)
                {
                    failure = stepResult;
                    break;
                }
            }

            var final = await SnapshotLockedAsync(session, new BrowserSnapshotArgs(), cancellationToken).ConfigureAwait(false);
            notices = MergeNotices(notices, final.Notices ?? Array.Empty<BrowserNoticeInfo>());
            if (!final.Ok) failure ??= final;

            var active = session.ActiveTabId is { } activeId && session.Tabs.TryGetValue(activeId, out var tab) ? tab : null;
            return new BrowserToolResult
            {
                Ok = failure is null,
                Code = failure?.Code ?? final.Code,
                Message = failure?.Message ?? final.Message,
                Permanent = failure?.Permanent ?? final.Permanent,
                TabId = active?.Id,
                Url = active?.Url,
                Title = active?.Title,
                Steps = results.ToArray(),
                Snapshot = final.Ok ? final.Snapshot : null,
                Truncated = final.Ok ? final.Truncated : null,
                RefCount = final.Ok ? final.RefCount : null,
                Notices = notices.Length > 0 ? notices : null,
            };
        }
        finally
        {
            session.Gate.Release();
        }
    }

    // ------------------------------------------------------------------ target + input helpers

    private async Task<TargetResult> PrepareTargetAsync(SessionState session, TabState tab, int refId, CancellationToken cancellationToken)
    {
        var notices = Array.Empty<BrowserNoticeInfo>();
        var frame = await CdpAsync(session, tab, "Page.getFrameTree", EmptyObject, ActionTimeout, cancellationToken).ConfigureAwait(false);
        if (frame.Failure is not null) return new TargetResult(null, 0, 0, null, MapCdpFailure(frame.Failure, frame.Notices), frame.Notices);
        notices = frame.Notices;

        var loaderId = GetFrameLoaderId(frame.Root);
        if (!string.IsNullOrEmpty(loaderId)
            && !string.IsNullOrEmpty(tab.LoaderId)
            && !string.Equals(loaderId, tab.LoaderId, StringComparison.Ordinal))
        {
            tab.Refs.Clear();
            tab.NextRef = 1;
            tab.LoaderId = loaderId;
            return new TargetResult(null, 0, 0, null,
                Fail("browser_ref_stale", "The page changed; take a new snapshot before acting.", permanent: true, notices),
                notices);
        }
        if (tab.LoaderId is null) tab.LoaderId = loaderId;

        var target = tab.Refs.Values.FirstOrDefault(item => item.Ref == refId);
        if (target is null)
        {
            return new TargetResult(null, 0, 0, null,
                Fail("browser_ref_unknown", $"Unknown ref {refId}; take a new snapshot.", permanent: true, notices),
                notices);
        }

        var scroll = await CdpAsync(session, tab, "DOM.scrollIntoViewIfNeeded",
            WriteObject(writer => writer.WriteNumber("backendNodeId", target.BackendNodeId)),
            ActionTimeout, cancellationToken).ConfigureAwait(false);
        if (scroll.Failure is not null) return new TargetResult(null, 0, 0, null, MapCdpFailure(scroll.Failure, MergeNotices(notices, scroll.Notices)), MergeNotices(notices, scroll.Notices));
        notices = MergeNotices(notices, scroll.Notices);

        var box = await CdpAsync(session, tab, "DOM.getBoxModel",
            WriteObject(writer => writer.WriteNumber("backendNodeId", target.BackendNodeId)),
            ActionTimeout, cancellationToken).ConfigureAwait(false);
        if (box.Failure is not null) return new TargetResult(null, 0, 0, null, MapCdpFailure(box.Failure, MergeNotices(notices, box.Notices)), MergeNotices(notices, box.Notices));
        notices = MergeNotices(notices, box.Notices);
        if (!TryGetContentCenter(box.Root, out var x, out var y))
        {
            return new TargetResult(null, 0, 0, null,
                Fail("browser_element_not_actionable", "The element has no visible box; take a new snapshot.", permanent: false, notices),
                notices);
        }

        var resolved = await CdpAsync(session, tab, "DOM.resolveNode",
            WriteObject(writer => writer.WriteNumber("backendNodeId", target.BackendNodeId)),
            ActionTimeout, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return new TargetResult(null, 0, 0, null, MapCdpFailure(resolved.Failure, MergeNotices(notices, resolved.Notices)), MergeNotices(notices, resolved.Notices));
        notices = MergeNotices(notices, resolved.Notices);
        var objectId = GetResolvedObjectId(resolved.Root);
        if (objectId is null)
        {
            return new TargetResult(null, 0, 0, null,
                Fail("browser_ref_stale", "The element is gone; take a new snapshot.", permanent: true, notices),
                notices);
        }

        var hit = await CdpAsync(session, tab, "Runtime.callFunctionOn",
            CallFunctionParams(objectId, HitTestFunction),
            ActionTimeout, cancellationToken).ConfigureAwait(false);
        if (hit.Failure is not null) return new TargetResult(null, 0, 0, null, WithNotices(hit.Failure, MergeNotices(notices, hit.Notices)), MergeNotices(notices, hit.Notices));
        notices = MergeNotices(notices, hit.Notices);
        var hitValue = GetByValue(hit.Root);
        var hitOk = hitValue is { ValueKind: JsonValueKind.Object } value
                    && value.TryGetProperty("ok", out var okElement)
                    && okElement.ValueKind == JsonValueKind.True;
        if (!hitOk)
        {
            var reason = hitValue is { ValueKind: JsonValueKind.Object } value2
                         && value2.TryGetProperty("reason", out var reasonElement)
                ? reasonElement.GetString()
                : null;
            return new TargetResult(null, 0, 0, null,
                Fail("browser_element_not_actionable",
                    $"The element cannot be clicked ({reason ?? "unknown"}); wait, scroll, or take a new snapshot.",
                    permanent: false,
                    notices),
                notices);
        }

        return new TargetResult(target, x, y, objectId, null, notices);
    }

    private async Task<(BrowserToolResult? Failure, BrowserNoticeInfo[] Notices)> DispatchMouseAsync(
        SessionState session,
        TabState tab,
        double x,
        double y,
        string button,
        int clickCount,
        CancellationToken cancellationToken)
    {
        var notices = Array.Empty<BrowserNoticeInfo>();
        var moved = await CdpAsync(session, tab, "Input.dispatchMouseEvent",
            WriteObject(writer =>
            {
                writer.WriteString("type", "mouseMoved");
                writer.WriteNumber("x", x);
                writer.WriteNumber("y", y);
                writer.WriteString("button", "none");
            }),
            ActionTimeout, cancellationToken).ConfigureAwait(false);
        notices = MergeNotices(notices, moved.Notices);
        if (moved.Failure is not null) return (moved.Failure, notices);

        var pressed = await CdpAsync(session, tab, "Input.dispatchMouseEvent",
            WriteObject(writer =>
            {
                writer.WriteString("type", "mousePressed");
                writer.WriteNumber("x", x);
                writer.WriteNumber("y", y);
                writer.WriteString("button", button);
                writer.WriteNumber("buttons", 1);
                writer.WriteNumber("clickCount", clickCount);
            }),
            ActionTimeout, cancellationToken).ConfigureAwait(false);
        notices = MergeNotices(notices, pressed.Notices);
        if (pressed.Failure is not null) return (pressed.Failure, notices);

        // Once the press is dispatched the input has happened; a failed release is reported, never
        // replayed as a second click.
        var released = await CdpAsync(session, tab, "Input.dispatchMouseEvent",
            WriteObject(writer =>
            {
                writer.WriteString("type", "mouseReleased");
                writer.WriteNumber("x", x);
                writer.WriteNumber("y", y);
                writer.WriteString("button", button);
                writer.WriteNumber("buttons", 0);
                writer.WriteNumber("clickCount", clickCount);
            }),
            ActionTimeout, cancellationToken).ConfigureAwait(false);
        notices = MergeNotices(notices, released.Notices);
        return (released.Failure, notices);
    }

    private async Task<(BrowserToolResult? Failure, BrowserNoticeInfo[] Notices)> DispatchKeyAsync(
        SessionState session,
        TabState tab,
        KeyStroke stroke,
        CancellationToken cancellationToken)
    {
        var baseParams = new Action<Utf8JsonWriter>(writer =>
        {
            writer.WriteString("key", stroke.Key);
            writer.WriteString("code", stroke.Code);
            writer.WriteNumber("windowsVirtualKeyCode", stroke.VirtualKey);
            if (stroke.Modifiers != 0) writer.WriteNumber("modifiers", stroke.Modifiers);
        });
        var down = await CdpAsync(session, tab, "Input.dispatchKeyEvent",
            WriteObject(writer =>
            {
                writer.WriteString("type", "keyDown");
                baseParams(writer);
                if (stroke.Text is not null) writer.WriteString("text", stroke.Text);
            }),
            ActionTimeout, cancellationToken).ConfigureAwait(false);
        var notices = down.Notices;
        if (down.Failure is not null) return (down.Failure, notices);
        var up = await CdpAsync(session, tab, "Input.dispatchKeyEvent",
            WriteObject(writer =>
            {
                writer.WriteString("type", "keyUp");
                baseParams(writer);
            }),
            ActionTimeout, cancellationToken).ConfigureAwait(false);
        return (up.Failure, MergeNotices(notices, up.Notices));
    }

    private async Task<(string? Value, BrowserToolResult? Failure, BrowserNoticeInfo[] Notices)> ReadValueAsync(
        SessionState session, TabState tab, string objectId, CancellationToken cancellationToken)
    {
        var call = await CallFunctionValueAsync(session, tab, objectId, ReadValueFunction, null, cancellationToken).ConfigureAwait(false);
        if (call.Failure is not null) return (null, call.Failure, call.Notices);
        var value = call.Value is { ValueKind: JsonValueKind.String } text ? text.GetString() : call.Value?.ToString();
        return (value, null, call.Notices);
    }

    private async Task<(JsonElement? Value, BrowserToolResult? Failure, BrowserNoticeInfo[] Notices)> CallFunctionValueAsync(
        SessionState session,
        TabState tab,
        string objectId,
        string function,
        JsonElement? argument,
        CancellationToken cancellationToken)
    {
        var call = await CdpAsync(session, tab, "Runtime.callFunctionOn",
            CallFunctionParams(objectId, function, argument),
            TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        if (call.Failure is not null) return (null, call.Failure, call.Notices);
        return (GetByValue(call.Root), null, call.Notices);
    }

    private static JsonElement CallFunctionParams(string objectId, string function, JsonElement? argument = null) =>
        WriteObject(writer =>
        {
            writer.WriteString("objectId", objectId);
            writer.WriteString("functionDeclaration", function);
            writer.WriteBoolean("awaitPromise", true);
            writer.WriteBoolean("returnByValue", true);
            if (argument is { } value)
            {
                writer.WritePropertyName("arguments");
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WritePropertyName("value");
                value.WriteTo(writer);
                writer.WriteEndObject();
                writer.WriteEndArray();
            }
        });

    private static JsonElement JsonString(string value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) writer.WriteStringValue(value);
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static string? GetResolvedObjectId(JsonElement root)
    {
        if (!root.TryGetProperty("object", out var objectElement) || objectElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return objectElement.TryGetProperty("objectId", out var objectId) && objectId.ValueKind == JsonValueKind.String
            ? objectId.GetString()
            : null;
    }

    private static bool TryGetContentCenter(JsonElement root, out double x, out double y)    {
        x = 0;
        y = 0;
        if (!root.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.Object) return false;
        if (!model.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return false;
        var values = new double[8];
        var index = 0;
        foreach (var item in content.EnumerateArray())
        {
            if (index >= 8 || !item.TryGetDouble(out var value)) return false;
            values[index++] = value;
        }
        if (index != 8) return false;
        x = (values[0] + values[2] + values[4] + values[6]) / 4;
        y = (values[1] + values[3] + values[5] + values[7]) / 4;
        return true;
    }

    private static BrowserToolResult MapCdpFailure(BrowserToolResult failure, BrowserNoticeInfo[] notices) =>
        string.Equals(failure.Code, "browser_cdp_error", StringComparison.Ordinal) && IsStaleMessage(failure.Message)
            ? Fail("browser_ref_stale", "The element is gone; take a new snapshot.", permanent: true, notices)
            : WithNotices(failure, notices);

    private static bool IsStaleMessage(string? message) =>
        message is not null
        && (message.Contains("No node with given id", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Could not find node", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Node is detached", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Cannot find context", StringComparison.OrdinalIgnoreCase));

    private static KeyStroke? ParseKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return null;
        var keyName = parts[^1];
        var modifiers = 0;
        foreach (var modifier in parts[..^1])
        {
            switch (modifier.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= 2;
                    break;
                case "shift":
                    modifiers |= 8;
                    break;
                case "alt":
                    modifiers |= 1;
                    break;
                case "meta":
                case "cmd":
                case "command":
                    modifiers |= 4;
                    break;
                case "controlormeta":
                    modifiers |= OperatingSystem.IsMacOS() ? 4 : 2;
                    break;
                default:
                    return null;
            }
        }

        if (keyName.Length == 1)
        {
            var character = keyName[0];
            var upper = char.ToUpperInvariant(character);
            var code = char.IsLetter(character) ? "Key" + upper : char.IsDigit(character) ? "Digit" + character : string.Empty;
            if (code.Length == 0) return null;
            return new KeyStroke(character.ToString(), code, upper, modifiers == 0 ? character.ToString() : null, modifiers);
        }

        return keyName.ToLowerInvariant() switch
        {
            "enter" or "return" => new KeyStroke("Enter", "Enter", 13, modifiers == 0 ? "\r" : null, modifiers),
            "tab" => new KeyStroke("Tab", "Tab", 9, null, modifiers),
            "escape" or "esc" => new KeyStroke("Escape", "Escape", 27, null, modifiers),
            "backspace" => new KeyStroke("Backspace", "Backspace", 8, null, modifiers),
            "delete" => new KeyStroke("Delete", "Delete", 46, null, modifiers),
            "arrowup" or "up" => new KeyStroke("ArrowUp", "ArrowUp", 38, null, modifiers),
            "arrowdown" or "down" => new KeyStroke("ArrowDown", "ArrowDown", 40, null, modifiers),
            "arrowleft" or "left" => new KeyStroke("ArrowLeft", "ArrowLeft", 37, null, modifiers),
            "arrowright" or "right" => new KeyStroke("ArrowRight", "ArrowRight", 39, null, modifiers),
            "home" => new KeyStroke("Home", "Home", 36, null, modifiers),
            "end" => new KeyStroke("End", "End", 35, null, modifiers),
            "pageup" => new KeyStroke("PageUp", "PageUp", 33, null, modifiers),
            "pagedown" => new KeyStroke("PageDown", "PageDown", 34, null, modifiers),
            "space" => new KeyStroke(" ", "Space", 32, modifiers == 0 ? " " : null, modifiers),
            _ => null,
        };
    }

    private static BrowserTabInfo[] TabInfos(SessionState session) =>
        session.Tabs.Values.Select(tab => new BrowserTabInfo
        {
            Id = tab.Id,
            Url = tab.Url,
            Title = tab.Title,
            Active = tab.Id == session.ActiveTabId,
        }).ToArray();

    private static string? NormalizeTabId(string? tabId)
    {
        var value = tabId?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 80) return null;
        foreach (var character in value)
        {
            if (!char.IsLetterOrDigit(character) && character is not ('.' or '_' or ':' or '-')) return null;
        }
        return value;
    }

    private static T StepArgs<T>(JsonElement? element, JsonTypeInfo<T> typeInfo) where T : new()
    {
        if (element is not { ValueKind: JsonValueKind.Object } value) return new T();
        try
        {
            return value.Deserialize(typeInfo) ?? new T();
        }
        catch (JsonException)
        {
            return new T();
        }
    }
}
