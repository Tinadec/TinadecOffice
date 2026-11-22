using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TinadecTools.Runtime;
using TinadecTools.Tools.FileRW;

namespace TinadecTools.Tools.Browser;

/// <summary>
/// Action, lifecycle, file-transfer, and declarative workflow half of <see cref="BrowserToolRuntime"/>.
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

    private const string SelectOptionFunction =
        "function(spec){" +
        "const el=this;" +
        "if(!el||el.tagName!=='SELECT')return {ok:false,reason:'not_select'};" +
        "const opts=Array.from(el.options||[]);" +
        "let matched=false;" +
        "if(Array.isArray(spec.values)){" +
        "const wanted=new Set(spec.values);" +
        "for(const o of opts){const hit=wanted.has(o.value)||wanted.has((o.label||o.text||'').trim());o.selected=hit;if(hit)matched=true;}" +
        "}else if(typeof spec.index==='number'){" +
        "if(spec.index>=0&&spec.index<opts.length){el.selectedIndex=spec.index;matched=true;}" +
        "}else if(typeof spec.label==='string'){" +
        "const idx=opts.findIndex(o=>(o.label||o.text||'').trim()===spec.label);if(idx>=0){el.selectedIndex=idx;matched=true;}" +
        "}else if(typeof spec.value==='string'){" +
        "let idx=opts.findIndex(o=>o.value===spec.value);" +
        "if(idx<0)idx=opts.findIndex(o=>(o.label||o.text||'').trim()===spec.value);" +
        "if(idx>=0){el.selectedIndex=idx;matched=true;}" +
        "}" +
        "if(!matched)return {ok:false,reason:'option_not_found'};" +
        "el.dispatchEvent(new Event('input',{bubbles:true}));" +
        "el.dispatchEvent(new Event('change',{bubbles:true}));" +
        "return {ok:true,value:el.value||''};" +
        "}";

    private const string FindElementFunction =
        "function(spec){" +
        "function norm(s){return (s||'').replace(/\\s+/g,' ').trim();}" +
        "function accName(el){" +
        "const al=el.getAttribute&&el.getAttribute('aria-label');if(al)return norm(al);" +
        "const lb=el.getAttribute&&el.getAttribute('aria-labelledby');" +
        "if(lb){const parts=lb.split(/\\s+/).map(id=>{const n=document.getElementById(id);return n?norm(n.textContent):'';}).filter(Boolean);if(parts.length)return parts.join(' ');}" +
        "if(el.labels&&el.labels.length){return norm(Array.from(el.labels).map(l=>l.textContent).join(' '));}" +
        "const ph=el.getAttribute&&el.getAttribute('placeholder');" +
        "const tag=(el.tagName||'').toLowerCase();" +
        "if(tag==='input'||tag==='textarea')return norm(ph||el.value||'');" +
        "return norm(el.innerText||el.textContent||el.getAttribute('title')||el.getAttribute('alt')||'');" +
        "}" +
        "function roleOf(el){" +
        "const r=el.getAttribute&&el.getAttribute('role');if(r)return r.toLowerCase();" +
        "const tag=(el.tagName||'').toLowerCase();" +
        "const type=((el.getAttribute&&el.getAttribute('type'))||'').toLowerCase();" +
        "if(tag==='button'||(tag==='input'&&(type==='button'||type==='submit'||type==='reset')))return 'button';" +
        "if(tag==='a'&&el.hasAttribute('href'))return 'link';" +
        "if(tag==='textarea')return 'textbox';" +
        "if(tag==='input'){if(type==='checkbox')return 'checkbox';if(type==='radio')return 'radio';if(type==='search')return 'searchbox';return 'textbox';}" +
        "if(tag==='select')return 'combobox';" +
        "if(tag==='option')return 'option';" +
        "return tag;" +
        "}" +
        "let list=[];" +
        "if(spec.kind==='css'){list=Array.from(document.querySelectorAll(spec.query));}" +
        "else if(spec.kind==='href'){list=Array.from(document.querySelectorAll('a[href]')).filter(a=>(a.getAttribute('href')||'').indexOf(spec.query)>=0||a.href.indexOf(spec.query)>=0);}" +
        "else if(spec.kind==='text'){" +
        "const all=Array.from(document.querySelectorAll('button,a,label,input,textarea,select,[role],h1,h2,h3,h4,p,span,div,li,td'));" +
        "const q=spec.exact?spec.query:norm(spec.query).toLowerCase();" +
        "list=all.filter(el=>{const t=spec.exact?(el.innerText||el.textContent||'').trim():norm(el.innerText||el.textContent||'').toLowerCase();return spec.exact?t===q:t.indexOf(q)>=0;});" +
        "if(list.length>1){const leaf=list.filter(el=>!list.some(other=>other!==el&&el.contains(other)));if(leaf.length===1)list=leaf;}" +
        "}else if(spec.kind==='role'){" +
        "const all=Array.from(document.querySelectorAll('*'));" +
        "list=all.filter(el=>{if(roleOf(el)!==spec.role)return false;if(!spec.name)return true;const n=accName(el);return spec.substring?n.toLowerCase().indexOf(spec.name.toLowerCase())>=0:n===spec.name;});" +
        "}" +
        "if(list.length!==1)return null;" +
        "return list[0];" +
        "}";

    private const string WaitSelectorStateFunction =
        "async function(css,state,timeoutMs){" +
        "const deadline=Date.now()+timeoutMs;" +
        "function isVisible(el){if(!el||!el.isConnected)return false;const r=el.getBoundingClientRect();if(r.width<1||r.height<1)return false;const s=getComputedStyle(el);return s.display!=='none'&&s.visibility!=='hidden';}" +
        "while(Date.now()<deadline){" +
        "let el=null;try{el=document.querySelector(css);}catch(e){return {ok:false,reason:'invalid_selector'};}" +
        "if(state==='attached'&&el)return {ok:true,reason:'attached'};" +
        "if(state==='visible'&&isVisible(el))return {ok:true,reason:'visible'};" +
        "if(state==='detached'&&!el)return {ok:true,reason:'detached'};" +
        "if(state==='hidden'&&(!el||!isVisible(el)))return {ok:true,reason:'hidden'};" +
        "await new Promise(function(r){setTimeout(r,100);});" +
        "}" +
        "return {ok:false,reason:'timeout'};" +
        "}";

    private const string ExtractFunction =
        "function(spec){" +
        "let nodes=[];" +
        "try{nodes=Array.from(document.querySelectorAll(spec.selector));}catch(e){return {ok:false,reason:'invalid_selector'};}" +
        "function readAttr(el,attr){" +
        "if(!attr||attr==='text'||attr==='textContent')return (el.textContent||'').trim();" +
        "if(attr==='innerText')return (el.innerText||el.textContent||'').trim();" +
        "if(attr==='value')return el.value!==undefined?String(el.value):'';" +
        "if(attr==='href'&&el.href)return String(el.href);" +
        "return (el.getAttribute&&el.getAttribute(attr))||'';" +
        "}" +
        "if(!spec.multiple){" +
        "if(nodes.length===0)return {ok:true,value:null};" +
        "return {ok:true,value:readAttr(nodes[0],spec.attribute)};" +
        "}" +
        "const capped=nodes.slice(0,spec.limit||20).map(n=>readAttr(n,spec.attribute));" +
        "return {ok:true,values:capped};" +
        "}";

    private const string FindFileInputFunction =
        "function(){" +
        "const el=this;" +
        "if(!el)return null;" +
        "if(el.tagName==='INPUT'&&String(el.type||'').toLowerCase()==='file')return el;" +
        "const list=el.querySelectorAll?Array.from(el.querySelectorAll('input[type=\"file\"]')):[];" +
        "return list.length===1?list[0]:null;" +
        "}";

    private static readonly HashSet<string> BatchTools = new(StringComparer.Ordinal)
    {
        "browser_click",
        "browser_type",
        "browser_press",
        "browser_scroll",
    };

    private static readonly HashSet<string> RunStepActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "goto",
        "fill",
        "click",
        "select_option",
        "press",
        "scroll",
        "wait_for_selector",
        "wait_for_url",
        "wait_for_text",
        "handle_dialog",
        "extract",
    };

    private static readonly HashSet<string> SafeExtractAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "text", "textcontent", "innertext", "value", "href", "src", "title", "alt",
        "aria-label", "placeholder", "name", "id", "role", "type",
    };

    private readonly record struct TargetResult(
        BrowserRef? Ref,
        double X,
        double Y,
        string? ObjectId,
        string? CdpSessionId,
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
        if ((args.Ref is null || args.Ref <= 0) && string.IsNullOrWhiteSpace(args.Selector))
        {
            return Fail("browser_ref_required", "A ref from browser_snapshot (or a selector) is required.", permanent: true);
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

        var beforeTabs = SnapshotTabIds(session);
        var consoleBaseline = session.ConsoleErrorCount;

        var target = await PrepareTargetOrSelectorAsync(session, tab, args.Ref, args.Selector, cancellationToken).ConfigureAwait(false);
        if (target.Failure is not null) return AttachReceipt(session, WithNotices(target.Failure, target.Notices), beforeTabs, consoleBaseline);

        var dispatch = await DispatchMouseAsync(session, tab, target.X, target.Y, button, clickCount, cancellationToken).ConfigureAwait(false);
        var notices = MergeNotices(MergeNotices(refresh.Notices, target.Notices), dispatch.Notices);
        if (dispatch.Failure is not null) return AttachReceipt(session, WithNotices(dispatch.Failure, notices), beforeTabs, consoleBaseline);

        await InspectPostActionStateAsync(session, cancellationToken).ConfigureAwait(false);
        return AttachReceipt(session, WithNotices(new BrowserToolResult
        {
            Ok = true,
            SpaceId = session.Id,
            Control = ControlName(session.Control),
            TabId = tab.Id,
            Url = tab.Url,
            Title = tab.Title,
            Ref = target.Ref?.Ref ?? args.Ref,
        }, notices), beforeTabs, consoleBaseline);
    }

    // ------------------------------------------------------------------ type / fill

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
        if ((args.Ref is null || args.Ref <= 0) && string.IsNullOrWhiteSpace(args.Selector))
        {
            return Fail("browser_ref_required", "A ref from browser_snapshot (or a selector) is required.", permanent: true);
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

        var beforeTabs = SnapshotTabIds(session);
        var consoleBaseline = session.ConsoleErrorCount;

        var target = await PrepareTargetOrSelectorAsync(session, tab, args.Ref, args.Selector, cancellationToken).ConfigureAwait(false);
        if (target.Failure is not null) return AttachReceipt(session, WithNotices(target.Failure, target.Notices), beforeTabs, consoleBaseline);
        var notices = MergeNotices(refresh.Notices, target.Notices);

        var focusParams = target.Ref is { BackendNodeId: > 0 } bRef
            ? WriteObject(writer => writer.WriteNumber("backendNodeId", bRef.BackendNodeId))
            : WriteObject(writer => writer.WriteString("objectId", target.ObjectId!));
        var focus = await CdpAsync(
            session, tab, "DOM.focus", focusParams, ActionTimeout, cancellationToken, cdpSessionId: target.CdpSessionId).ConfigureAwait(false);
        if (focus.Failure is not null) return AttachReceipt(session, WithNotices(focus.Failure, MergeNotices(notices, focus.Notices)), beforeTabs, consoleBaseline);
        notices = MergeNotices(notices, focus.Notices);

        var selected = await CallFunctionValueAsync(
            session, tab, target.ObjectId!, SelectFunction, null, cancellationToken, cdpSessionId: target.CdpSessionId).ConfigureAwait(false);
        if (selected.Failure is not null) return AttachReceipt(session, WithNotices(selected.Failure, MergeNotices(notices, selected.Notices)), beforeTabs, consoleBaseline);
        notices = MergeNotices(notices, selected.Notices);

        var insert = await CdpAsync(session, tab, "Input.insertText",
            WriteObject(writer => writer.WriteString("text", text)),
            ActionTimeout, cancellationToken).ConfigureAwait(false);
        if (insert.Failure is not null) return AttachReceipt(session, WithNotices(insert.Failure, MergeNotices(notices, insert.Notices)), beforeTabs, consoleBaseline);
        notices = MergeNotices(notices, insert.Notices);

        var actual = await ReadValueAsync(session, tab, target.ObjectId!, cancellationToken, cdpSessionId: target.CdpSessionId).ConfigureAwait(false);
        if (actual.Value is not null || actual.Failure is null)
        {
            notices = MergeNotices(notices, actual.Notices);
        }
        if (actual.Failure is not null) return AttachReceipt(session, WithNotices(actual.Failure, notices), beforeTabs, consoleBaseline);

        if (!string.Equals(actual.Value, text, StringComparison.Ordinal))
        {
            var fallback = await CallFunctionValueAsync(
                session, tab, target.ObjectId!, SetValueFunction, JsonString(text), cancellationToken, cdpSessionId: target.CdpSessionId).ConfigureAwait(false);
            notices = MergeNotices(notices, fallback.Notices);
            if (fallback.Failure is not null) return AttachReceipt(session, WithNotices(fallback.Failure, notices), beforeTabs, consoleBaseline);
            var reread = await ReadValueAsync(session, tab, target.ObjectId!, cancellationToken, cdpSessionId: target.CdpSessionId).ConfigureAwait(false);
            notices = MergeNotices(notices, reread.Notices);
            if (reread.Failure is not null) return AttachReceipt(session, WithNotices(reread.Failure, notices), beforeTabs, consoleBaseline);
            actual = reread;
        }

        if (args.Submit == true)
        {
            var submit = await DispatchKeyAsync(session, tab, ParseKey("Enter")!.Value, cancellationToken).ConfigureAwait(false);
            notices = MergeNotices(notices, submit.Notices);
            if (submit.Failure is not null) return AttachReceipt(session, WithNotices(submit.Failure, notices), beforeTabs, consoleBaseline);
        }

        await InspectPostActionStateAsync(session, cancellationToken).ConfigureAwait(false);
        return AttachReceipt(session, WithNotices(new BrowserToolResult
        {
            Ok = true,
            SpaceId = session.Id,
            Control = ControlName(session.Control),
            TabId = tab.Id,
            Url = tab.Url,
            Title = tab.Title,
            Ref = target.Ref?.Ref ?? args.Ref,
            Value = actual.Value,
            Verified = string.Equals(actual.Value, text, StringComparison.Ordinal),
        }, notices), beforeTabs, consoleBaseline);
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

        var beforeTabs = SnapshotTabIds(session);
        var consoleBaseline = session.ConsoleErrorCount;

        if ((args.Ref is > 0) || !string.IsNullOrWhiteSpace(args.Selector))
        {
            var target = await PrepareTargetOrSelectorAsync(session, tab, args.Ref, args.Selector, cancellationToken).ConfigureAwait(false);
            if (target.Failure is not null) return AttachReceipt(session, WithNotices(target.Failure, target.Notices), beforeTabs, consoleBaseline);
            notices = MergeNotices(notices, target.Notices);
            var focusParams = target.Ref is { BackendNodeId: > 0 } bRef
                ? WriteObject(writer => writer.WriteNumber("backendNodeId", bRef.BackendNodeId))
                : WriteObject(writer => writer.WriteString("objectId", target.ObjectId!));
            var focus = await CdpAsync(
                session, tab, "DOM.focus", focusParams, ActionTimeout, cancellationToken, cdpSessionId: target.CdpSessionId).ConfigureAwait(false);
            if (focus.Failure is not null) return AttachReceipt(session, WithNotices(focus.Failure, MergeNotices(notices, focus.Notices)), beforeTabs, consoleBaseline);
            notices = MergeNotices(notices, focus.Notices);
        }

        var dispatch = await DispatchKeyAsync(session, tab, stroke.Value, cancellationToken).ConfigureAwait(false);
        notices = MergeNotices(notices, dispatch.Notices);
        if (dispatch.Failure is not null) return AttachReceipt(session, WithNotices(dispatch.Failure, notices), beforeTabs, consoleBaseline);

        await InspectPostActionStateAsync(session, cancellationToken).ConfigureAwait(false);
        return AttachReceipt(session, WithNotices(new BrowserToolResult
        {
            Ok = true,
            SpaceId = session.Id,
            Control = ControlName(session.Control),
            TabId = tab.Id,
            Url = tab.Url,
            Title = tab.Title,
            Ref = args.Ref,
        }, notices), beforeTabs, consoleBaseline);
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

        var beforeTabs = SnapshotTabIds(session);
        var consoleBaseline = session.ConsoleErrorCount;

        double x;
        double y;
        if ((args.Ref is > 0) || !string.IsNullOrWhiteSpace(args.Selector))
        {
            var target = await PrepareTargetOrSelectorAsync(session, tab, args.Ref, args.Selector, cancellationToken).ConfigureAwait(false);
            if (target.Failure is not null) return AttachReceipt(session, WithNotices(target.Failure, target.Notices), beforeTabs, consoleBaseline);
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
            if (metrics.Failure is not null) return AttachReceipt(session, WithNotices(metrics.Failure, MergeNotices(notices, metrics.Notices)), beforeTabs, consoleBaseline);
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
        if (wheel.Failure is not null) return AttachReceipt(session, WithNotices(wheel.Failure, notices), beforeTabs, consoleBaseline);

        return AttachReceipt(session, WithNotices(new BrowserToolResult
        {
            Ok = true,
            SpaceId = session.Id,
            Control = ControlName(session.Control),
            TabId = tab.Id,
            Url = tab.Url,
            Title = tab.Title,
            Ref = args.Ref,
        }, notices), beforeTabs, consoleBaseline);
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

            var beforeTabs = SnapshotTabIds(session);
            var consoleBaseline = session.ConsoleErrorCount;

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
                if (history.Failure is not null) return AttachReceipt(session, WithNotices(history.Failure, MergeNotices(notices, history.Notices)), beforeTabs, consoleBaseline);
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
                    return AttachReceipt(session, WithNotices(Fail("browser_history_unavailable", $"There is no {action} entry.", permanent: false), notices), beforeTabs, consoleBaseline);
                }
                var entry = entries.EnumerateArray().ElementAt(targetIndex);
                var entryId = entry.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var entryNumber)
                    ? entryNumber
                    : -1;
                if (entryId < 0)
                {
                    return AttachReceipt(session, WithNotices(Fail("browser_history_unavailable", "The history entry has no id.", permanent: false), notices), beforeTabs, consoleBaseline);
                }
                navigation = await CdpAsync(session, tab, "Page.navigateToHistoryEntry",
                    WriteObject(writer => writer.WriteNumber("entryId", entryId)),
                    TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            }

            notices = MergeNotices(notices, navigation.Notices);
            if (navigation.Failure is not null) return AttachReceipt(session, WithNotices(navigation.Failure, notices), beforeTabs, consoleBaseline);
            tab.Refs.Clear();
            tab.NextRef = 1;
            tab.LoaderId = null;

            return AttachReceipt(session, WithNotices(new BrowserToolResult
            {
                Ok = true,
                SpaceId = session.Id,
                Control = ControlName(session.Control),
                TabId = tab.Id,
                Url = tab.Url,
                Title = tab.Title,
            }, notices), beforeTabs, consoleBaseline);
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
            var beforeTabs = SnapshotTabIds(session);
            var consoleBaseline = session.ConsoleErrorCount;

            var opened = await _bridge.SendAsync(
                new BrowserBridgeCall("tabs/open", session.Id, Url: url?.ToString()),
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);
            if (!opened.Ok) return FromBridge(session, opened);
            RecordConsoleErrors(session, opened.Data);
            var notices = MergeNotices(refresh.Notices, CollectNewNotices(session, opened.Data ?? EmptyObject));
            var after = await RefreshAsync(session, cancellationToken, allowOpenDialog: true).ConfigureAwait(false);
            if (after.Failure is not null) return AttachReceipt(session, WithNotices(after.Failure, MergeNotices(notices, after.Notices)), beforeTabs, consoleBaseline);
            notices = MergeNotices(notices, after.Notices);
            return AttachReceipt(session, WithNotices(new BrowserToolResult
            {
                Ok = true,
                SpaceId = session.Id,
                Control = ControlName(session.Control),
                TabId = session.ActiveTabId,
                Tabs = TabInfos(session),
            }, notices), beforeTabs, consoleBaseline);
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
            if (!selected.Ok) return FromBridge(session, selected);
            var notices = MergeNotices(refresh.Notices, CollectNewNotices(session, selected.Data ?? EmptyObject));
            var after = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
            if (after.Failure is not null) return WithNotices(after.Failure, MergeNotices(notices, after.Notices));
            return WithNotices(new BrowserToolResult
            {
                Ok = true,
                SpaceId = session.Id,
                Control = ControlName(session.Control),
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
            if (!closed.Ok) return FromBridge(session, closed);
            var notices = MergeNotices(refresh.Notices, CollectNewNotices(session, closed.Data ?? EmptyObject));
            var after = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
            if (after.Failure is not null) return WithNotices(after.Failure, MergeNotices(notices, after.Notices));
            return WithNotices(new BrowserToolResult
            {
                Ok = true,
                SpaceId = session.Id,
                Control = ControlName(session.Control),
                TabId = session.ActiveTabId,
                Tabs = TabInfos(session),
            }, MergeNotices(notices, after.Notices));
        }
        finally
        {
            session.Gate.Release();
        }
    }

    // ------------------------------------------------------------------ handoff / takeover / finish / dialog

    public async Task<BrowserToolResult> HandOffAsync(string? sessionId, BrowserHandoffArgs args, CancellationToken cancellationToken)
    {
        var id = NormalizeSessionId(sessionId);
        if (id is null) return Fail("browser_session_invalid", "A session id is required.", permanent: true);

        var session = GetSession(id);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.Control == TaskSpaceControlState.Finished)
            {
                return FailTaskSpaceFinished(session.Id);
            }

            var reason = string.IsNullOrWhiteSpace(args.Reason) ? "manual_takeover" : args.Reason.Trim();
            var response = await _bridge.SendAsync(
                new BrowserBridgeCall("handoff", session.Id, Reason: reason),
                ActionTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!response.Ok && !string.Equals(response.Code, "browser_bridge_bad_op", StringComparison.Ordinal))
            {
                return FromBridge(session, response);
            }

            session.Control = TaskSpaceControlState.User;
            var userNote = string.IsNullOrWhiteSpace(args.Message)
                ? "Browser control handed off to the user. Wait for the user to confirm completion before calling browser_takeover."
                : args.Message.Trim();

            return new BrowserToolResult
            {
                Ok = true,
                Code = "browser_handoff_requested",
                Message = userNote,
                SpaceId = session.Id,
                Control = "user",
                Reason = reason,
                TabId = session.ActiveTabId,
                Tabs = TabInfos(session),
            };
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> TakeOverAsync(string? sessionId, BrowserTakeoverArgs args, CancellationToken cancellationToken)
    {
        var id = NormalizeSessionId(sessionId);
        if (id is null) return Fail("browser_session_invalid", "A session id is required.", permanent: true);

        var session = GetSession(id);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Refuse resuming a finished/ended task space cleanly per ego-errors.ts (EGO_TASK_SPACE_INACTIVE).
            if (session.Control == TaskSpaceControlState.Finished)
            {
                return FailTaskSpaceFinished(session.Id);
            }

            var response = await _bridge.SendAsync(
                new BrowserBridgeCall("takeover", session.Id),
                ActionTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!response.Ok && !string.Equals(response.Code, "browser_bridge_bad_op", StringComparison.Ordinal))
            {
                return FromBridge(session, response);
            }

            session.Control = TaskSpaceControlState.Agent;
            foreach (var tab in session.Tabs.Values)
            {
                tab.Refs.Clear();
                tab.NextRef = 1;
                tab.LoaderId = null;
            }

            await InspectPostActionStateAsync(session, cancellationToken).ConfigureAwait(false);
            var active = session.ActiveTabId is { } activeId && session.Tabs.TryGetValue(activeId, out var t) ? t : null;
            return new BrowserToolResult
            {
                Ok = true,
                Code = "browser_control_resumed",
                Message = "Agent control resumed. Take a fresh browser_snapshot before acting on page elements.",
                SpaceId = session.Id,
                Control = "agent",
                TabId = active?.Id,
                Url = active?.Url,
                Title = active?.Title,
                Tabs = TabInfos(session),
                PendingDialog = session.PendingDialog,
            };
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> FinishAsync(string? sessionId, BrowserFinishArgs args, CancellationToken cancellationToken)
    {
        var id = NormalizeSessionId(sessionId);
        if (id is null) return Fail("browser_session_invalid", "A session id is required.", permanent: true);

        var session = GetSession(id);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var guard = CheckLocalControlBoundary(session);
            if (guard is not null) return guard;

            var keepSet = new HashSet<string>(
                (args.Keep ?? Array.Empty<string>())
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .Select(k => k.Trim()),
                StringComparer.Ordinal);

            var response = await _bridge.SendAsync(
                new BrowserBridgeCall("finish", session.Id, Keep: keepSet.ToArray()),
                ActionTimeout,
                cancellationToken).ConfigureAwait(false);

            string[] keptTabs;
            string[] closedTabs;
            if (response.Ok && response.Data is { ValueKind: JsonValueKind.Object } data)
            {
                keptTabs = ReadStringArray(data, "kept_tabs");
                closedTabs = ReadStringArray(data, "closed_tabs");
                SyncTabs(session, ParseTabs(data), GetString(data, "active_tab"));
            }
            else if (string.Equals(response.Code, "browser_bridge_bad_op", StringComparison.Ordinal))
            {
                // Fallback for minimal mock bridges that only implement tabs/list & tabs/close.
                await InspectPostActionStateAsync(session, cancellationToken).ConfigureAwait(false);
                var existingIds = session.Tabs.Keys.ToArray();
                var closed = new List<string>();
                var kept = new List<string>();
                foreach (var tabId in existingIds)
                {
                    if (keepSet.Contains(tabId))
                    {
                        kept.Add(tabId);
                    }
                    else
                    {
                        var closeResp = await _bridge.SendAsync(
                            new BrowserBridgeCall("tabs/close", session.Id, TabId: tabId),
                            ActionTimeout,
                            cancellationToken).ConfigureAwait(false);
                        if (closeResp.Ok)
                        {
                            closed.Add(tabId);
                            session.Tabs.Remove(tabId);
                        }
                    }
                }
                keptTabs = kept.ToArray();
                closedTabs = closed.ToArray();
            }
            else
            {
                return FromBridge(session, response);
            }

            foreach (var tab in session.Tabs.Values)
            {
                tab.Refs.Clear();
                tab.NextRef = 1;
                tab.LoaderId = null;
            }
            session.Control = TaskSpaceControlState.Finished;

            return new BrowserToolResult
            {
                Ok = true,
                Code = "browser_task_space_completed",
                Message = keptTabs.Length > 0
                    ? $"Task space finished; kept {keptTabs.Length} tab(s) open for the user."
                    : "Task space finished and intermediate tabs closed.",
                SpaceId = session.Id,
                Control = "finished",
                KeptTabs = keptTabs,
                ClosedTabs = closedTabs,
            };
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> DialogAsync(string? sessionId, BrowserDialogArgs args, CancellationToken cancellationToken)
    {
        var id = NormalizeSessionId(sessionId);
        if (id is null) return Fail("browser_session_invalid", "A session id is required.", permanent: true);
        if (args.Accept is not { } accept)
        {
            return Fail("browser_dialog_invalid", "accept (true or false) is required.", permanent: true);
        }
        if (args.PromptText is { Length: > 4096 })
        {
            return Fail("browser_dialog_invalid", "prompt_text exceeds 4096 characters.", permanent: true);
        }

        var session = GetSession(id);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await DialogLockedAsync(session, accept, args.PromptText, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async Task<BrowserToolResult> DialogLockedAsync(
        SessionState session,
        bool accept,
        string? promptText,
        CancellationToken cancellationToken)
    {
        var guard = CheckLocalControlBoundary(session);
        if (guard is not null) return guard;

        var response = await _bridge.SendAsync(
            new BrowserBridgeCall("dialog", session.Id, Accept: accept, PromptText: promptText),
            ActionTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!response.Ok) return FromBridge(session, response);

        session.PendingDialog = null;
        if (response.Data is { ValueKind: JsonValueKind.Object } data)
        {
            RecordConsoleErrors(session, data);
            SyncTabs(session, ParseTabs(data), GetString(data, "active_tab"));
        }

        var active = session.ActiveTabId is { } activeId && session.Tabs.TryGetValue(activeId, out var t) ? t : null;
        return new BrowserToolResult
        {
            Ok = true,
            SpaceId = session.Id,
            Control = ControlName(session.Control),
            TabId = active?.Id,
            Url = active?.Url,
            Title = active?.Title,
        };
    }

    // ------------------------------------------------------------------ upload & download_save

    public async Task<BrowserToolResult> UploadAsync(string? sessionId, BrowserUploadArgs args, CancellationToken cancellationToken)
    {
        var id = NormalizeSessionId(sessionId);
        if (id is null) return Fail("browser_session_invalid", "A session id is required.", permanent: true);

        var rawPaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(args.Path)) rawPaths.Add(args.Path);
        if (args.Paths is { Length: > 0 })
        {
            rawPaths.AddRange(args.Paths.Where(p => !string.IsNullOrWhiteSpace(p)));
        }
        if (rawPaths.Count == 0)
        {
            return Fail("browser_upload_invalid", "Provide path or paths inside the allowed workspace.", permanent: true);
        }

        var resolvedPaths = new List<string>(rawPaths.Count);
        foreach (var raw in rawPaths)
        {
            string resolved;
            try
            {
                resolved = WorkspacePathResolver.ResolvePath(raw, allowFinalLink: false, writable: false);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException)
            {
                return Fail("browser_path_denied", ex.Message, permanent: true);
            }

            if (!File.Exists(resolved))
            {
                return Fail("browser_file_not_found", $"Upload file '{raw}' does not exist.", permanent: true);
            }
            resolvedPaths.Add(resolved);
        }

        var session = GetSession(id);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var refresh = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
            if (refresh.Failure is not null) return refresh.Failure;
            var missing = RequireActiveTab(session, out var tab);
            if (missing is not null) return WithNotices(missing, refresh.Notices);

            var beforeTabs = SnapshotTabIds(session);
            var consoleBaseline = session.ConsoleErrorCount;

            var selector = string.IsNullOrWhiteSpace(args.Selector) && (args.Ref is null || args.Ref <= 0)
                ? "input[type=\"file\"]"
                : args.Selector;

            var resolvedNode = await ResolveTargetObjectAsync(session, tab, args.Ref, selector, cancellationToken).ConfigureAwait(false);
            if (resolvedNode.Failure is not null)
            {
                return AttachReceipt(session, WithNotices(resolvedNode.Failure, MergeNotices(refresh.Notices, resolvedNode.Notices)), beforeTabs, consoleBaseline);
            }
            var notices = MergeNotices(refresh.Notices, resolvedNode.Notices);

            // Ensure target is (or uniquely wraps) an <input type="file">.
            var fileInputObj = await CdpAsync(
                session,
                tab,
                "Runtime.callFunctionOn",
                WriteObject(w =>
                {
                    w.WriteString("objectId", resolvedNode.ObjectId!);
                    w.WriteString("functionDeclaration", FindFileInputFunction);
                    w.WriteBoolean("returnByValue", false);
                }),
                ActionTimeout,
                cancellationToken,
                cdpSessionId: resolvedNode.CdpSessionId).ConfigureAwait(false);
            notices = MergeNotices(notices, fileInputObj.Notices);
            var effectiveObjectId = GetObjectId(fileInputObj.Root) ?? resolvedNode.ObjectId!;

            var setFiles = await CdpAsync(
                session,
                tab,
                "DOM.setFileInputFiles",
                WriteObject(w =>
                {
                    w.WriteString("objectId", effectiveObjectId);
                    w.WritePropertyName("files");
                    w.WriteStartArray();
                    foreach (var file in resolvedPaths) w.WriteStringValue(file);
                    w.WriteEndArray();
                }),
                TimeSpan.FromSeconds(20),
                cancellationToken,
                cdpSessionId: resolvedNode.CdpSessionId).ConfigureAwait(false);
            notices = MergeNotices(notices, setFiles.Notices);
            if (setFiles.Failure is not null)
            {
                return AttachReceipt(session, WithNotices(setFiles.Failure, notices), beforeTabs, consoleBaseline);
            }

            await InspectPostActionStateAsync(session, cancellationToken).ConfigureAwait(false);
            return AttachReceipt(session, WithNotices(new BrowserToolResult
            {
                Ok = true,
                SpaceId = session.Id,
                Control = ControlName(session.Control),
                TabId = tab.Id,
                Url = tab.Url,
                Title = tab.Title,
                Ref = resolvedNode.Ref?.Ref ?? args.Ref,
                Path = resolvedPaths[0],
            }, notices), beforeTabs, consoleBaseline);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> DownloadSaveAsync(string? sessionId, BrowserDownloadSaveArgs args, CancellationToken cancellationToken)
    {
        var id = NormalizeSessionId(sessionId);
        if (id is null) return Fail("browser_session_invalid", "A session id is required.", permanent: true);
        if (string.IsNullOrWhiteSpace(args.SavePath))
        {
            return Fail("browser_download_invalid", "save_path inside the writable workspace is required.", permanent: true);
        }

        string resolvedSavePath;
        try
        {
            resolvedSavePath = WorkspacePathResolver.ResolvePath(args.SavePath, allowFinalLink: false, writable: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException)
        {
            return Fail("browser_path_denied", ex.Message, permanent: true);
        }

        Uri? triggerUrl = null;
        if (!string.IsNullOrWhiteSpace(args.Url))
        {
            if (!CurrentNavigationPolicy().TryNormalize(args.Url, out var normalizedUrl, out var navError))
            {
                return Fail("browser_navigation_blocked", navError, permanent: true);
            }
            triggerUrl = normalizedUrl;
        }

        if (triggerUrl is null && (args.Ref is null || args.Ref <= 0) && string.IsNullOrWhiteSpace(args.Selector))
        {
            return Fail("browser_download_invalid", "Provide url, ref, or selector to trigger the download.", permanent: true);
        }

        var timeoutMs = Math.Clamp(args.TimeoutMs ?? 30_000, 500, 120_000);
        var session = GetSession(id);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var refresh = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
            if (refresh.Failure is not null) return refresh.Failure;

            if (session.ActiveTabId is null && triggerUrl is not null)
            {
                var opened = await _bridge.SendAsync(
                    new BrowserBridgeCall("tabs/open", session.Id),
                    TimeSpan.FromSeconds(30),
                    cancellationToken).ConfigureAwait(false);
                if (!opened.Ok) return FromBridge(session, opened);
                refresh = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
                if (refresh.Failure is not null) return refresh.Failure;
            }

            var missing = RequireActiveTab(session, out var tab);
            if (missing is not null) return WithNotices(missing, refresh.Notices);

            var beforeTabs = SnapshotTabIds(session);
            var consoleBaseline = session.ConsoleErrorCount;
            var stagingDir = Path.Combine(Path.GetTempPath(), "tinadec-browser-dl-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDir);

            try
            {
                var arm = await CdpAsync(
                    session,
                    tab,
                    "Page.setDownloadBehavior",
                    WriteObject(w =>
                    {
                        w.WriteString("behavior", "allowAndName");
                        w.WriteString("downloadPath", stagingDir);
                    }),
                    ActionTimeout,
                    cancellationToken).ConfigureAwait(false);
                if (arm.Failure is not null) return AttachReceipt(session, WithNotices(arm.Failure, MergeNotices(refresh.Notices, arm.Notices)), beforeTabs, consoleBaseline);

                if (triggerUrl is not null)
                {
                    var nav = await CdpAsync(
                        session,
                        tab,
                        "Page.navigate",
                        WriteObject(w => w.WriteString("url", triggerUrl.ToString())),
                        TimeSpan.FromSeconds(30),
                        cancellationToken).ConfigureAwait(false);
                    if (nav.Failure is not null) return AttachReceipt(session, WithNotices(nav.Failure, MergeNotices(refresh.Notices, nav.Notices)), beforeTabs, consoleBaseline);
                }
                else
                {
                    var clickOutcome = await ClickLockedAsync(
                        session,
                        new BrowserClickArgs { Ref = args.Ref, Selector = args.Selector },
                        cancellationToken).ConfigureAwait(false);
                    if (!clickOutcome.Ok) return clickOutcome;
                }

                var completedFile = await WaitForDownloadedFileAsync(stagingDir, timeoutMs, cancellationToken).ConfigureAwait(false);
                if (completedFile is null)
                {
                    return AttachReceipt(
                        session,
                        Fail("browser_download_timeout", $"No completed download appeared within {timeoutMs} ms.", permanent: false),
                        beforeTabs,
                        consoleBaseline);
                }

                var fileInfo = new FileInfo(completedFile);
                ToolExecutionContext.CheckFileSize("write", fileInfo.Length);
                var targetDir = Path.GetDirectoryName(resolvedSavePath);
                if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
                File.Copy(completedFile, resolvedSavePath, overwrite: true);

                await InspectPostActionStateAsync(session, cancellationToken).ConfigureAwait(false);
                return AttachReceipt(session, new BrowserToolResult
                {
                    Ok = true,
                    SpaceId = session.Id,
                    Control = ControlName(session.Control),
                    TabId = tab.Id,
                    Url = tab.Url,
                    Title = tab.Title,
                    Path = resolvedSavePath,
                    Bytes = fileInfo.Length,
                    SuggestedFilename = Path.GetFileName(completedFile),
                }, beforeTabs, consoleBaseline);
            }
            finally
            {
                _ = await CdpAsync(
                    session,
                    tab,
                    "Page.setDownloadBehavior",
                    WriteObject(w => w.WriteString("behavior", "default")),
                    TimeSpan.FromSeconds(2),
                    CancellationToken.None).ConfigureAwait(false);
                try { Directory.Delete(stagingDir, recursive: true); } catch { /* best effort */ }
            }
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private static async Task<string?> WaitForDownloadedFileAsync(string stagingDir, int timeoutMs, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(stagingDir))
            {
                var files = Directory.GetFiles(stagingDir);
                var finished = files.FirstOrDefault(f =>
                    !f.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase)
                    && !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                    && !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
                if (finished is not null) return finished;
            }
            await Task.Delay(75, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    // ------------------------------------------------------------------ browser_run (governed workflow)

    public async Task<BrowserToolResult> RunAsync(string? sessionId, BrowserRunArgs args, CancellationToken cancellationToken)
    {
        var id = NormalizeSessionId(sessionId);
        if (id is null) return Fail("browser_session_invalid", "A session id is required.", permanent: true);
        if (args.Steps is not { Length: > 0 and <= 30 })
        {
            return Fail("browser_run_invalid", "steps must contain between 1 and 30 workflow steps.", permanent: true);
        }

        // Pre-validate every step AND enforce BrowserNavigationPolicy on every `goto` step BEFORE executing anything!
        var policy = CurrentNavigationPolicy();
        var normalizedGotoUris = new Uri?[args.Steps.Length];
        for (var i = 0; i < args.Steps.Length; i++)
        {
            var step = args.Steps[i];
            var action = step.Action?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(action) || !RunStepActions.Contains(action))
            {
                return Fail("browser_run_invalid", $"Step {i} has unsupported action '{step.Action}'.", permanent: true);
            }

            if (action == "goto")
            {
                if (!policy.TryNormalize(step.Url, out var uri, out var navError))
                {
                    return Fail("browser_navigation_blocked", $"Step {i} (goto): {navError}", permanent: true);
                }
                normalizedGotoUris[i] = uri;
            }
            else if (action == "extract")
            {
                if (string.IsNullOrWhiteSpace(step.Selector))
                {
                    return Fail("browser_run_invalid", $"Step {i} (extract) requires selector.", permanent: true);
                }
                if (!IsAllowedExtractAttribute(step.Attribute))
                {
                    return Fail("browser_run_invalid", $"Step {i} (extract) attribute '{step.Attribute}' is not permitted.", permanent: true);
                }
            }
            else if (action == "handle_dialog" && step.Accept is null)
            {
                return Fail("browser_run_invalid", $"Step {i} (handle_dialog) requires accept (boolean).", permanent: true);
            }
        }

        var session = GetSession(id);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var guard = CheckLocalControlBoundary(session);
            if (guard is not null) return guard;

            var beforeTabs = SnapshotTabIds(session);
            var consoleBaseline = session.ConsoleErrorCount;
            var stepResults = new List<BrowserRunStepResult>(args.Steps.Length);
            var extractions = new List<BrowserExtractionResult>();
            var notices = Array.Empty<BrowserNoticeInfo>();
            BrowserToolResult? failure = null;

            for (var i = 0; i < args.Steps.Length; i++)
            {
                var step = args.Steps[i];
                var action = step.Action!.Trim().ToLowerInvariant();

                // If the previous step opened a JS dialog and this step is NOT handle_dialog, stop immediately with receipt.
                if (session.PendingDialog is { } openDialog && action != "handle_dialog")
                {
                    failure = FailDialogOpen(session, openDialog, notices);
                    stepResults.Add(new BrowserRunStepResult
                    {
                        Index = i,
                        Action = action,
                        Ok = false,
                        Code = failure.Code,
                        Message = failure.Message,
                    });
                    break;
                }

                var outcome = await ExecuteRunStepLockedAsync(session, step, action, normalizedGotoUris[i], extractions, cancellationToken)
                    .ConfigureAwait(false);
                notices = MergeNotices(notices, outcome.Notices ?? Array.Empty<BrowserNoticeInfo>());

                // If this step opened a JS dialog AND the very next step is `handle_dialog`, let `handle_dialog` handle it!
                var nextIsDialogHandler = i + 1 < args.Steps.Length
                    && string.Equals(args.Steps[i + 1].Action?.Trim(), "handle_dialog", StringComparison.OrdinalIgnoreCase);
                if (!outcome.Ok && string.Equals(outcome.Code, "browser_dialog_open", StringComparison.Ordinal) && nextIsDialogHandler)
                {
                    stepResults.Add(new BrowserRunStepResult
                    {
                        Index = i,
                        Action = action,
                        Ok = true,
                        Code = "browser_dialog_opened",
                        Message = outcome.Message,
                    });
                    continue;
                }

                stepResults.Add(new BrowserRunStepResult
                {
                    Index = i,
                    Action = action,
                    Ok = outcome.Ok,
                    Code = outcome.Code,
                    Message = outcome.Message,
                    Url = outcome.Url,
                    Value = outcome.Value,
                });

                if (!outcome.Ok)
                {
                    failure = outcome;
                    break;
                }
            }

            BrowserToolResult? finalSnapshot = null;
            if (args.IncludeSnapshot != false
                && session.Control == TaskSpaceControlState.Agent
                && session.PendingDialog is null
                && session.ActiveTabId is not null)
            {
                finalSnapshot = await SnapshotLockedAsync(
                    session,
                    new BrowserSnapshotArgs
                    {
                        Scope = args.SnapshotScope ?? "viewport",
                        MaxChars = args.MaxSnapshotChars,
                    },
                    cancellationToken).ConfigureAwait(false);
                // If mock bridge does not implement Page.getLayoutMetrics for viewport scope, fall back to full_page cleanly.
                if (!finalSnapshot.Ok && string.IsNullOrEmpty(args.SnapshotScope))
                {
                    finalSnapshot = await SnapshotLockedAsync(
                        session,
                        new BrowserSnapshotArgs { Scope = "full_page", MaxChars = args.MaxSnapshotChars },
                        cancellationToken).ConfigureAwait(false);
                }
                notices = MergeNotices(notices, finalSnapshot.Notices ?? Array.Empty<BrowserNoticeInfo>());
                if (!finalSnapshot.Ok) failure ??= finalSnapshot;
            }

            BrowserToolResult? finalShot = null;
            if (args.IncludeScreenshot == true
                && failure is null
                && session.Control == TaskSpaceControlState.Agent
                && session.PendingDialog is null
                && session.ActiveTabId is not null)
            {
                finalShot = await ScreenshotLockedAsync(
                    session,
                    new BrowserScreenshotArgs { FullPage = args.FullPageScreenshot == true },
                    cancellationToken).ConfigureAwait(false);
                notices = MergeNotices(notices, finalShot.Notices ?? Array.Empty<BrowserNoticeInfo>());
                if (!finalShot.Ok) failure ??= finalShot;
            }

            var activeTab = session.ActiveTabId is { } activeId && session.Tabs.TryGetValue(activeId, out var t) ? t : null;
            var combined = new BrowserToolResult
            {
                Ok = failure is null,
                Code = failure?.Code,
                Message = failure?.Message,
                Permanent = failure?.Permanent,
                SpaceId = session.Id,
                Control = ControlName(session.Control),
                TabId = activeTab?.Id,
                Url = activeTab?.Url,
                Title = activeTab?.Title,
                RunSteps = stepResults.ToArray(),
                Extractions = extractions.Count > 0 ? extractions.ToArray() : null,
                Snapshot = finalSnapshot is { Ok: true } ? finalSnapshot.Snapshot : null,
                Truncated = finalSnapshot is { Ok: true } ? finalSnapshot.Truncated : null,
                RefCount = finalSnapshot is { Ok: true } ? finalSnapshot.RefCount : null,
                Path = finalShot is { Ok: true } ? finalShot.Path : null,
                ScreenshotPath = finalShot is { Ok: true } ? finalShot.ScreenshotPath : null,
                Bytes = finalShot is { Ok: true } ? finalShot.Bytes : null,
                SiteLearningPath = TryFindSiteLearningPath(activeTab?.Url),
                Notices = notices.Length > 0 ? notices : null,
                PendingDialog = failure?.PendingDialog ?? session.PendingDialog,
            };
            return AttachReceipt(session, combined, beforeTabs, consoleBaseline);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async Task<BrowserToolResult> ExecuteRunStepLockedAsync(
        SessionState session,
        BrowserRunStep step,
        string action,
        Uri? gotoUri,
        List<BrowserExtractionResult> extractions,
        CancellationToken cancellationToken)
    {
        return action switch
        {
            "goto" => await NavigateLockedAsync(session, gotoUri!, step.WaitMs, cancellationToken).ConfigureAwait(false),
            "fill" => await TypeLockedAsync(
                session,
                new BrowserTypeArgs { Ref = step.Ref, Selector = step.Selector, Text = step.Text, Submit = step.Submit },
                cancellationToken).ConfigureAwait(false),
            "click" => await ClickLockedAsync(
                session,
                new BrowserClickArgs { Ref = step.Ref, Selector = step.Selector, Button = step.Button, ClickCount = step.ClickCount },
                cancellationToken).ConfigureAwait(false),
            "select_option" => await SelectOptionLockedAsync(session, step, cancellationToken).ConfigureAwait(false),
            "press" => await PressLockedAsync(
                session,
                new BrowserPressArgs { Key = step.Key, Ref = step.Ref, Selector = step.Selector },
                cancellationToken).ConfigureAwait(false),
            "scroll" => await ScrollLockedAsync(
                session,
                new BrowserScrollArgs { Ref = step.Ref, Selector = step.Selector, DeltaX = step.DeltaX, DeltaY = step.DeltaY },
                cancellationToken).ConfigureAwait(false),
            "wait_for_selector" => await WaitForSelectorLockedAsync(session, step, cancellationToken).ConfigureAwait(false),
            "wait_for_url" => await WaitLockedAsync(
                session,
                new BrowserWaitArgs { UrlContains = step.UrlContains ?? step.Url, TimeoutMs = step.TimeoutMs },
                cancellationToken).ConfigureAwait(false),
            "wait_for_text" => await WaitLockedAsync(
                session,
                new BrowserWaitArgs { Text = step.Text, TimeoutMs = step.TimeoutMs },
                cancellationToken).ConfigureAwait(false),
            "handle_dialog" => await DialogLockedAsync(session, step.Accept == true, step.PromptText, cancellationToken).ConfigureAwait(false),
            "extract" => await ExtractLockedAsync(session, step, extractions, cancellationToken).ConfigureAwait(false),
            _ => Fail("browser_run_invalid", $"Unsupported step action '{action}'.", permanent: true),
        };
    }

    private async Task<BrowserToolResult> SelectOptionLockedAsync(
        SessionState session,
        BrowserRunStep step,
        CancellationToken cancellationToken)
    {
        var refresh = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
        if (refresh.Failure is not null) return refresh.Failure;
        var missing = RequireActiveTab(session, out var tab);
        if (missing is not null) return WithNotices(missing, refresh.Notices);

        var target = await ResolveTargetObjectAsync(session, tab, step.Ref, step.Selector, cancellationToken).ConfigureAwait(false);
        if (target.Failure is not null) return WithNotices(target.Failure, MergeNotices(refresh.Notices, target.Notices));

        var arg = WriteObject(w =>
        {
            if (step.Values is { Length: > 0 })
            {
                w.WritePropertyName("values");
                w.WriteStartArray();
                foreach (var v in step.Values) w.WriteStringValue(v);
                w.WriteEndArray();
            }
            if (step.Index is { } idx) w.WriteNumber("index", idx);
            if (step.Label is not null) w.WriteString("label", step.Label);
            if (step.Value is not null) w.WriteString("value", step.Value);
        });

        var call = await CallFunctionValueAsync(
            session, tab, target.ObjectId!, SelectOptionFunction, arg, cancellationToken, cdpSessionId: target.CdpSessionId).ConfigureAwait(false);
        var notices = MergeNotices(MergeNotices(refresh.Notices, target.Notices), call.Notices);
        if (call.Failure is not null) return WithNotices(call.Failure, notices);

        var val = call.Value;
        var ok = val is { ValueKind: JsonValueKind.Object } o
            && o.TryGetProperty("ok", out var okEl)
            && okEl.ValueKind == JsonValueKind.True;
        if (!ok)
        {
            var reason = val is { ValueKind: JsonValueKind.Object } o2 ? GetString(o2, "reason") : null;
            return WithNotices(
                Fail("browser_select_failed", $"Could not select option ({reason ?? "unknown"}).", permanent: false),
                notices);
        }

        var selectedValue = val is { ValueKind: JsonValueKind.Object } o3 ? GetString(o3, "value") : null;
        return WithNotices(new BrowserToolResult { Ok = true, TabId = tab.Id, Value = selectedValue }, notices);
    }

    private async Task<BrowserToolResult> WaitForSelectorLockedAsync(
        SessionState session,
        BrowserRunStep step,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(step.Selector))
        {
            return Fail("browser_wait_invalid", "wait_for_selector requires selector.", permanent: true);
        }

        var state = (step.State ?? "visible").Trim().ToLowerInvariant();
        if (state is not ("attached" or "visible" or "hidden" or "detached"))
        {
            return Fail("browser_wait_invalid", "state must be attached, visible, hidden, or detached.", permanent: true);
        }

        var css = StripCssLocatorPrefix(step.Selector) ?? step.Selector;
        var timeoutMs = Math.Clamp(step.TimeoutMs ?? 15_000, 100, 60_000);
        var refresh = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
        if (refresh.Failure is not null) return refresh.Failure;
        var missing = RequireActiveTab(session, out var tab);
        if (missing is not null) return WithNotices(missing, refresh.Notices);

        var window = await GetWindowObjectIdAsync(session, tab, cancellationToken).ConfigureAwait(false);
        if (window.Failure is not null) return WithNotices(window.Failure, MergeNotices(refresh.Notices, window.Notices));
        if (window.ObjectId is null) return Fail("browser_wait_failed", "Could not reach page execution context.", permanent: false);

        var call = await CdpAsync(
            session,
            tab,
            "Runtime.callFunctionOn",
            WriteObject(w =>
            {
                w.WriteString("objectId", window.ObjectId);
                w.WriteString("functionDeclaration", WaitSelectorStateFunction);
                w.WriteBoolean("awaitPromise", true);
                w.WriteBoolean("returnByValue", true);
                w.WritePropertyName("arguments");
                w.WriteStartArray();
                WriteRemoteString(w, css);
                WriteRemoteString(w, state);
                w.WriteStartObject();
                w.WriteNumber("value", timeoutMs);
                w.WriteEndObject();
                w.WriteEndArray();
            }),
            TimeSpan.FromMilliseconds(timeoutMs + 15_000),
            cancellationToken).ConfigureAwait(false);
        var notices = MergeNotices(MergeNotices(refresh.Notices, window.Notices), call.Notices);
        if (call.Failure is not null) return WithNotices(call.Failure, notices);

        var val = GetByValue(call.Root);
        var ok = val is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
        var reason = val is { ValueKind: JsonValueKind.Object } o2 ? GetString(o2, "reason") : null;
        if (!ok && string.Equals(reason, "invalid_selector", StringComparison.Ordinal))
        {
            return WithNotices(Fail("browser_selector_invalid", $"Invalid CSS selector '{css}'.", permanent: true), notices);
        }

        return ok
            ? WithNotices(new BrowserToolResult { Ok = true, TabId = tab.Id, Reason = reason }, notices)
            : WithNotices(Fail("browser_wait_timeout", $"Selector '{css}' did not reach state '{state}' within {timeoutMs} ms.", permanent: false), notices);
    }

    private async Task<BrowserToolResult> ExtractLockedAsync(
        SessionState session,
        BrowserRunStep step,
        List<BrowserExtractionResult> extractions,
        CancellationToken cancellationToken)
    {
        var css = StripCssLocatorPrefix(step.Selector!) ?? step.Selector!;
        var refresh = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
        if (refresh.Failure is not null) return refresh.Failure;
        var missing = RequireActiveTab(session, out var tab);
        if (missing is not null) return WithNotices(missing, refresh.Notices);

        var window = await GetWindowObjectIdAsync(session, tab, cancellationToken).ConfigureAwait(false);
        if (window.Failure is not null) return WithNotices(window.Failure, MergeNotices(refresh.Notices, window.Notices));
        if (window.ObjectId is null) return Fail("browser_extract_failed", "Could not reach page execution context.", permanent: false);

        var spec = WriteObject(w =>
        {
            w.WriteString("selector", css);
            if (!string.IsNullOrWhiteSpace(step.Attribute)) w.WriteString("attribute", step.Attribute.Trim());
            w.WriteBoolean("multiple", step.Multiple == true);
            w.WriteNumber("limit", Math.Clamp(step.Limit ?? 20, 1, 100));
        });

        var call = await CallFunctionValueAsync(session, tab, window.ObjectId, ExtractFunction, spec, cancellationToken).ConfigureAwait(false);
        var notices = MergeNotices(MergeNotices(refresh.Notices, window.Notices), call.Notices);
        if (call.Failure is not null) return WithNotices(call.Failure, notices);

        var val = call.Value;
        var ok = val is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
        if (!ok)
        {
            return WithNotices(Fail("browser_selector_invalid", $"Invalid extract selector '{css}'.", permanent: true), notices);
        }

        var singleVal = val is { ValueKind: JsonValueKind.Object } o2 ? GetString(o2, "value") : null;
        string[]? multiVals = null;
        if (val is { ValueKind: JsonValueKind.Object } o3
            && o3.TryGetProperty("values", out var arr)
            && arr.ValueKind == JsonValueKind.Array)
        {
            multiVals = arr.EnumerateArray()
                .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? string.Empty : x.ToString())
                .ToArray();
        }

        extractions.Add(new BrowserExtractionResult
        {
            Name = string.IsNullOrWhiteSpace(step.Name) ? $"step_{extractions.Count}" : step.Name.Trim(),
            Selector = css,
            Value = singleVal,
            Values = multiVals,
        });

        return WithNotices(new BrowserToolResult
        {
            Ok = true,
            TabId = tab.Id,
            Value = singleVal,
        }, notices);
    }

    private static bool IsAllowedExtractAttribute(string? attr)
    {
        if (string.IsNullOrWhiteSpace(attr)) return true;
        var trimmed = attr.Trim();
        return SafeExtractAttributes.Contains(trimmed)
            || trimmed.StartsWith("data-", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("aria-", StringComparison.OrdinalIgnoreCase);
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

            var beforeTabs = SnapshotTabIds(session);
            var consoleBaseline = session.ConsoleErrorCount;
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

            var final = session.PendingDialog is null
                ? await SnapshotLockedAsync(session, new BrowserSnapshotArgs(), cancellationToken).ConfigureAwait(false)
                : FailDialogOpen(session, session.PendingDialog, notices);
            notices = MergeNotices(notices, final.Notices ?? Array.Empty<BrowserNoticeInfo>());
            if (!final.Ok) failure ??= final;

            var active = session.ActiveTabId is { } activeId && session.Tabs.TryGetValue(activeId, out var tab) ? tab : null;
            return AttachReceipt(session, new BrowserToolResult
            {
                Ok = failure is null,
                Code = failure?.Code ?? final.Code,
                Message = failure?.Message ?? final.Message,
                Permanent = failure?.Permanent ?? final.Permanent,
                SpaceId = session.Id,
                Control = ControlName(session.Control),
                TabId = active?.Id,
                Url = active?.Url,
                Title = active?.Title,
                Steps = results.ToArray(),
                Snapshot = final.Ok ? final.Snapshot : null,
                Truncated = final.Ok ? final.Truncated : null,
                RefCount = final.Ok ? final.RefCount : null,
                Notices = notices.Length > 0 ? notices : null,
            }, beforeTabs, consoleBaseline);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    // ------------------------------------------------------------------ target + selector + input helpers

    private async Task<TargetResult> PrepareTargetOrSelectorAsync(
        SessionState session,
        TabState tab,
        int? refId,
        string? selector,
        CancellationToken cancellationToken)
    {
        if (refId is > 0)
        {
            return await PrepareTargetAsync(session, tab, refId.Value, cancellationToken).ConfigureAwait(false);
        }

        if (TryParseInlineRef(selector, out var parsedRef))
        {
            return await PrepareTargetAsync(session, tab, parsedRef, cancellationToken).ConfigureAwait(false);
        }

        var resolved = await ResolveSelectorObjectIdAsync(session, tab, selector!, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved;

        var notices = resolved.Notices;
        var scroll = await CdpAsync(
            session,
            tab,
            "DOM.scrollIntoViewIfNeeded",
            WriteObject(w => w.WriteString("objectId", resolved.ObjectId!)),
            ActionTimeout,
            cancellationToken,
            cdpSessionId: resolved.CdpSessionId).ConfigureAwait(false);
        notices = MergeNotices(notices, scroll.Notices);
        if (scroll.Failure is not null) return new TargetResult(null, 0, 0, null, null, MapCdpFailure(scroll.Failure, notices), notices);

        var box = await CdpAsync(
            session,
            tab,
            "DOM.getBoxModel",
            WriteObject(w => w.WriteString("objectId", resolved.ObjectId!)),
            ActionTimeout,
            cancellationToken,
            cdpSessionId: resolved.CdpSessionId).ConfigureAwait(false);
        notices = MergeNotices(notices, box.Notices);
        if (box.Failure is not null) return new TargetResult(null, 0, 0, null, null, MapCdpFailure(box.Failure, notices), notices);

        if (!TryGetContentCenter(box.Root, out var x, out var y))
        {
            return new TargetResult(null, 0, 0, null, null,
                Fail("browser_element_not_actionable", "The element has no visible box.", permanent: false, notices),
                notices);
        }

        var hit = await CdpAsync(
            session,
            tab,
            "Runtime.callFunctionOn",
            CallFunctionParams(resolved.ObjectId!, HitTestFunction),
            ActionTimeout,
            cancellationToken,
            cdpSessionId: resolved.CdpSessionId).ConfigureAwait(false);
        notices = MergeNotices(notices, hit.Notices);
        if (hit.Failure is not null) return new TargetResult(null, 0, 0, null, null, WithNotices(hit.Failure, notices), notices);

        var hitValue = GetByValue(hit.Root);
        var hitOk = hitValue is { ValueKind: JsonValueKind.Object } hv
            && hv.TryGetProperty("ok", out var okEl)
            && okEl.ValueKind == JsonValueKind.True;
        if (!hitOk)
        {
            var reason = hitValue is { ValueKind: JsonValueKind.Object } hv2 ? GetString(hv2, "reason") : null;
            return new TargetResult(null, 0, 0, null, null,
                Fail("browser_element_not_actionable",
                    $"The element cannot be acted on ({reason ?? "unknown"}); wait or scroll first.",
                    permanent: false,
                    notices),
                notices);
        }

        return new TargetResult(null, x, y, resolved.ObjectId, resolved.CdpSessionId, null, notices);
    }

    private async Task<TargetResult> ResolveTargetObjectAsync(
        SessionState session,
        TabState tab,
        int? refId,
        string? selector,
        CancellationToken cancellationToken)
    {
        if (refId is > 0 || TryParseInlineRef(selector, out _))
        {
            var effectiveRef = refId is > 0 ? refId.Value : (TryParseInlineRef(selector, out var r) ? r : 0);
            if (!tab.Refs.TryGetValue(effectiveRef, out var target))
            {
                return new TargetResult(null, 0, 0, null, null,
                    Fail("browser_ref_unknown", $"Unknown ref {effectiveRef}; take a new snapshot.", permanent: true),
                    Array.Empty<BrowserNoticeInfo>());
            }

            var resolved = await CdpAsync(
                session,
                tab,
                "DOM.resolveNode",
                WriteObject(w => w.WriteNumber("backendNodeId", target.BackendNodeId)),
                ActionTimeout,
                cancellationToken,
                cdpSessionId: target.CdpSessionId).ConfigureAwait(false);
            if (resolved.Failure is not null)
            {
                return new TargetResult(null, 0, 0, null, null, MapCdpFailure(resolved.Failure, resolved.Notices), resolved.Notices);
            }
            var objectId = GetResolvedObjectId(resolved.Root);
            if (objectId is null)
            {
                return new TargetResult(null, 0, 0, null, null,
                    Fail("browser_ref_stale", "The element is gone; take a new snapshot.", permanent: true, resolved.Notices),
                    resolved.Notices);
            }
            return new TargetResult(target, 0, 0, objectId, target.CdpSessionId, null, resolved.Notices);
        }

        return await ResolveSelectorObjectIdAsync(session, tab, selector ?? string.Empty, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TargetResult> ResolveSelectorObjectIdAsync(
        SessionState session,
        TabState tab,
        string rawSelector,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawSelector))
        {
            return new TargetResult(null, 0, 0, null, null,
                Fail("browser_selector_invalid", "A non-empty selector is required.", permanent: true),
                Array.Empty<BrowserNoticeInfo>());
        }

        var spec = ParseSelectorSpec(rawSelector);
        if (spec is null)
        {
            return new TargetResult(null, 0, 0, null, null,
                Fail("browser_selector_invalid", $"Unsupported selector '{rawSelector}'.", permanent: true),
                Array.Empty<BrowserNoticeInfo>());
        }

        var window = await GetWindowObjectIdAsync(session, tab, cancellationToken).ConfigureAwait(false);
        if (window.Failure is not null) return new TargetResult(null, 0, 0, null, null, window.Failure, window.Notices);
        if (window.ObjectId is null)
        {
            return new TargetResult(null, 0, 0, null, null,
                Fail("browser_element_not_found", $"Element not found for '{rawSelector}'.", permanent: false, window.Notices),
                window.Notices);
        }

        var found = await CdpAsync(
            session,
            tab,
            "Runtime.callFunctionOn",
            WriteObject(w =>
            {
                w.WriteString("objectId", window.ObjectId);
                w.WriteString("functionDeclaration", FindElementFunction);
                w.WriteBoolean("returnByValue", false);
                w.WritePropertyName("arguments");
                w.WriteStartArray();
                w.WriteStartObject();
                w.WritePropertyName("value");
                spec.Value.WriteTo(w);
                w.WriteEndObject();
                w.WriteEndArray();
            }),
            ActionTimeout,
            cancellationToken).ConfigureAwait(false);
        var notices = MergeNotices(window.Notices, found.Notices);
        if (found.Failure is not null) return new TargetResult(null, 0, 0, null, null, found.Failure, notices);

        var objId = GetObjectId(found.Root);
        if (objId is null)
        {
            return new TargetResult(null, 0, 0, null, null,
                Fail("browser_element_not_found", $"Selector '{rawSelector}' did not match a unique element.", permanent: false, notices),
                notices);
        }

        return new TargetResult(null, 0, 0, objId, null, null, notices);
    }

    private static bool TryParseInlineRef(string? selector, out int refId)
    {
        refId = 0;
        if (string.IsNullOrWhiteSpace(selector)) return false;
        var trimmed = selector.Trim();
        if (trimmed.StartsWith('@') && int.TryParse(trimmed[1..], out refId) && refId > 0) return true;
        if (trimmed.StartsWith("ref=", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(trimmed[4..], out refId)
            && refId > 0)
        {
            return true;
        }
        return false;
    }

    private static JsonElement? ParseSelectorSpec(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("loc=role:", StringComparison.OrdinalIgnoreCase))
        {
            var body = trimmed["loc=role:".Length..].Trim();
            var bracketIdx = body.IndexOf('[');
            var role = (bracketIdx >= 0 ? body[..bracketIdx] : body).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(role)) return null;
            string? name = null;
            var substring = false;
            if (bracketIdx >= 0 && body.EndsWith(']'))
            {
                var inside = body[(bracketIdx + 1)..^1].Trim();
                if (inside.StartsWith("name*=", StringComparison.OrdinalIgnoreCase))
                {
                    substring = true;
                    name = UnquoteSelectorValue(inside["name*=".Length..]);
                }
                else if (inside.StartsWith("name=", StringComparison.OrdinalIgnoreCase))
                {
                    name = UnquoteSelectorValue(inside["name=".Length..]);
                }
            }
            return WriteObject(w =>
            {
                w.WriteString("kind", "role");
                w.WriteString("role", role);
                if (name is not null) w.WriteString("name", name);
                w.WriteBoolean("substring", substring);
            });
        }

        if (trimmed.StartsWith("loc=href:", StringComparison.OrdinalIgnoreCase))
        {
            var href = trimmed["loc=href:".Length..].Trim();
            return string.IsNullOrEmpty(href)
                ? null
                : WriteObject(w =>
                {
                    w.WriteString("kind", "href");
                    w.WriteString("query", href);
                });
        }

        if (trimmed.StartsWith("text=", StringComparison.OrdinalIgnoreCase))
        {
            var textBody = trimmed["text=".Length..].Trim();
            var exact = (textBody.StartsWith('"') && textBody.EndsWith('"') && textBody.Length >= 2)
                || (textBody.StartsWith('\'') && textBody.EndsWith('\'') && textBody.Length >= 2);
            var query = exact ? UnquoteSelectorValue(textBody) : textBody;
            return string.IsNullOrEmpty(query)
                ? null
                : WriteObject(w =>
                {
                    w.WriteString("kind", "text");
                    w.WriteString("query", query);
                    w.WriteBoolean("exact", exact);
                });
        }

        var css = StripCssLocatorPrefix(trimmed) ?? trimmed;
        return string.IsNullOrEmpty(css)
            ? null
            : WriteObject(w =>
            {
                w.WriteString("kind", "css");
                w.WriteString("query", css);
            });
    }

    private static string UnquoteSelectorValue(string raw)
    {
        var s = raw.Trim();
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
        {
            s = s[1..^1];
        }
        return s.Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\'", "'", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    private async Task<TargetResult> PrepareTargetAsync(SessionState session, TabState tab, int refId, CancellationToken cancellationToken)
    {
        var notices = Array.Empty<BrowserNoticeInfo>();
        var frame = await CdpAsync(session, tab, "Page.getFrameTree", EmptyObject, ActionTimeout, cancellationToken).ConfigureAwait(false);
        if (frame.Failure is not null) return new TargetResult(null, 0, 0, null, null, MapCdpFailure(frame.Failure, frame.Notices), frame.Notices);
        notices = frame.Notices;

        var loaderId = GetFrameLoaderId(frame.Root);
        if (!string.IsNullOrEmpty(loaderId)
            && !string.IsNullOrEmpty(tab.LoaderId)
            && !string.Equals(loaderId, tab.LoaderId, StringComparison.Ordinal))
        {
            tab.Refs.Clear();
            tab.NextRef = 1;
            tab.LoaderId = loaderId;
            return new TargetResult(null, 0, 0, null, null,
                Fail("browser_ref_stale", "The page changed; take a new snapshot before acting.", permanent: true, notices),
                notices);
        }
        if (tab.LoaderId is null) tab.LoaderId = loaderId;

        var target = tab.Refs.TryGetValue(refId, out var direct)
            ? direct
            : tab.Refs.Values.FirstOrDefault(item => item.Ref == refId);
        if (target is null)
        {
            return new TargetResult(null, 0, 0, null, null,
                Fail("browser_ref_unknown", $"Unknown ref {refId}; take a new snapshot.", permanent: true, notices),
                notices);
        }

        var cdpSessionId = target.CdpSessionId;
        var scroll = await CdpAsync(session, tab, "DOM.scrollIntoViewIfNeeded",
            WriteObject(writer => writer.WriteNumber("backendNodeId", target.BackendNodeId)),
            ActionTimeout, cancellationToken, cdpSessionId: cdpSessionId).ConfigureAwait(false);
        if (scroll.Failure is not null) return new TargetResult(null, 0, 0, null, null, MapCdpFailure(scroll.Failure, MergeNotices(notices, scroll.Notices)), MergeNotices(notices, scroll.Notices));
        notices = MergeNotices(notices, scroll.Notices);

        var box = await CdpAsync(session, tab, "DOM.getBoxModel",
            WriteObject(writer => writer.WriteNumber("backendNodeId", target.BackendNodeId)),
            ActionTimeout, cancellationToken, cdpSessionId: cdpSessionId).ConfigureAwait(false);
        if (box.Failure is not null) return new TargetResult(null, 0, 0, null, null, MapCdpFailure(box.Failure, MergeNotices(notices, box.Notices)), MergeNotices(notices, box.Notices));
        notices = MergeNotices(notices, box.Notices);
        if (!TryGetContentCenter(box.Root, out var x, out var y))
        {
            return new TargetResult(null, 0, 0, null, null,
                Fail("browser_element_not_actionable", "The element has no visible box; take a new snapshot.", permanent: false, notices),
                notices);
        }

        // Translate OOPIF-local coordinates into top-level page coordinates via owning iframe box when needed.
        if (!string.IsNullOrEmpty(cdpSessionId) && !string.IsNullOrEmpty(target.FrameId))
        {
            var ownerBackend = await TryGetFrameOwnerBackendIdAsync(session, tab, target.FrameId, cancellationToken).ConfigureAwait(false);
            if (ownerBackend is > 0 and var ownerId)
            {
                var ownerBox = await CdpAsync(
                    session,
                    tab,
                    "DOM.getBoxModel",
                    WriteObject(w => w.WriteNumber("backendNodeId", ownerId)),
                    TimeSpan.FromSeconds(3),
                    cancellationToken).ConfigureAwait(false);
                if (ownerBox.Failure is null && TryGetBoxBounds(ownerBox.Root, out var ox, out var oy, out _, out _))
                {
                    x += ox;
                    y += oy;
                }
            }
        }

        var resolved = await CdpAsync(session, tab, "DOM.resolveNode",
            WriteObject(writer => writer.WriteNumber("backendNodeId", target.BackendNodeId)),
            ActionTimeout, cancellationToken, cdpSessionId: cdpSessionId).ConfigureAwait(false);
        if (resolved.Failure is not null) return new TargetResult(null, 0, 0, null, null, MapCdpFailure(resolved.Failure, MergeNotices(notices, resolved.Notices)), MergeNotices(notices, resolved.Notices));
        notices = MergeNotices(notices, resolved.Notices);
        var objectId = GetResolvedObjectId(resolved.Root);
        if (objectId is null)
        {
            return new TargetResult(null, 0, 0, null, null,
                Fail("browser_ref_stale", "The element is gone; take a new snapshot.", permanent: true, notices),
                notices);
        }

        var hit = await CdpAsync(session, tab, "Runtime.callFunctionOn",
            CallFunctionParams(objectId, HitTestFunction),
            ActionTimeout, cancellationToken, cdpSessionId: cdpSessionId).ConfigureAwait(false);
        if (hit.Failure is not null) return new TargetResult(null, 0, 0, null, null, WithNotices(hit.Failure, MergeNotices(notices, hit.Notices)), MergeNotices(notices, hit.Notices));
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
            return new TargetResult(null, 0, 0, null, null,
                Fail("browser_element_not_actionable",
                    $"The element cannot be clicked ({reason ?? "unknown"}); wait, scroll, or take a new snapshot.",
                    permanent: false,
                    notices),
                notices);
        }

        return new TargetResult(target, x, y, objectId, cdpSessionId, null, notices);
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
        SessionState session,
        TabState tab,
        string objectId,
        CancellationToken cancellationToken,
        string? cdpSessionId = null)
    {
        var call = await CallFunctionValueAsync(
            session, tab, objectId, ReadValueFunction, null, cancellationToken, cdpSessionId: cdpSessionId).ConfigureAwait(false);
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
        CancellationToken cancellationToken,
        string? cdpSessionId = null)
    {
        var call = await CdpAsync(session, tab, "Runtime.callFunctionOn",
            CallFunctionParams(objectId, function, argument),
            TimeSpan.FromSeconds(15), cancellationToken, cdpSessionId: cdpSessionId).ConfigureAwait(false);
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

    private static bool TryGetContentCenter(JsonElement root, out double x, out double y)
    {
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

    private static string[] ReadStringArray(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(property, out var arr)
            || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }
        return arr.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(item.GetString()))
            .Select(item => item.GetString()!)
            .ToArray();
    }

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
