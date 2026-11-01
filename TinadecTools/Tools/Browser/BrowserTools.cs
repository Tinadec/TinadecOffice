using System.Text.Json;
using NLog;
using TinadecTools.Abstractions;

namespace TinadecTools.Tools.Browser;

/// <summary>
/// Registers the browser tools by hand because they need the raw request's session id (the
/// source-generated typed wrapper only receives arguments). P2 exposes the read surface plus
/// navigation; clicking/typing/batching land in P3.
/// </summary>
public static class BrowserToolRegistration
{
    public const string SnapshotToolId = "browser_snapshot";
    public const string NavigateToolId = "browser_navigate";
    public const string TabsListToolId = "browser_tabs_list";
    public const string WaitToolId = "browser_wait";
    public const string ScreenshotToolId = "browser_screenshot";
    public const string ClickToolId = "browser_click";
    public const string TypeToolId = "browser_type";
    public const string PressToolId = "browser_press";
    public const string ScrollToolId = "browser_scroll";
    public const string HistoryToolId = "browser_history";
    public const string TabOpenToolId = "browser_tab_open";
    public const string TabSelectToolId = "browser_tab_select";
    public const string TabCloseToolId = "browser_tab_close";
    public const string BatchToolId = "browser_batch";

    private const string SnapshotSchema =
        """{"type":"object","properties":{"max_chars":{"type":"integer","minimum":2048,"maximum":64000}},"additionalProperties":false}""";

    private const string NavigateSchema =
        """{"type":"object","properties":{"url":{"type":"string"},"wait_ms":{"type":"integer","minimum":0,"maximum":30000}},"required":["url"],"additionalProperties":false}""";

    private const string TabsListSchema =
        """{"type":"object","properties":{},"additionalProperties":false}""";

    private const string WaitSchema =
        """{"type":"object","properties":{"text":{"type":"string"},"url_contains":{"type":"string"},"timeout_ms":{"type":"integer","minimum":100,"maximum":60000}},"additionalProperties":false}""";

    private const string ScreenshotSchema =
        """{"type":"object","properties":{"full_page":{"type":"boolean"}},"additionalProperties":false}""";

    private const string ClickSchema =
        """{"type":"object","properties":{"ref":{"type":"integer","minimum":1},"button":{"type":"string","enum":["left","right","middle"]},"click_count":{"type":"integer","minimum":1,"maximum":2}},"required":["ref"],"additionalProperties":false}""";

    private const string TypeSchema =
        """{"type":"object","properties":{"ref":{"type":"integer","minimum":1},"text":{"type":"string"},"submit":{"type":"boolean"}},"required":["ref"],"additionalProperties":false}""";

    private const string PressSchema =
        """{"type":"object","properties":{"key":{"type":"string"},"ref":{"type":"integer","minimum":1}},"required":["key"],"additionalProperties":false}""";

    private const string ScrollSchema =
        """{"type":"object","properties":{"ref":{"type":"integer","minimum":1},"delta_x":{"type":"number"},"delta_y":{"type":"number"}},"additionalProperties":false}""";

    private const string HistorySchema =
        """{"type":"object","properties":{"action":{"type":"string","enum":["back","forward","reload"]}},"required":["action"],"additionalProperties":false}""";

    private const string TabOpenSchema =
        """{"type":"object","properties":{"url":{"type":"string"}},"additionalProperties":false}""";

    private const string TabIdSchema =
        """{"type":"object","properties":{"tab_id":{"type":"string"}},"required":["tab_id"],"additionalProperties":false}""";

    private const string BatchSchema =
        """{"type":"object","properties":{"steps":{"type":"array","minItems":1,"maxItems":20,"items":{"type":"object","properties":{"tool":{"type":"string","enum":["browser_click","browser_type","browser_press","browser_scroll"]},"args":{"type":"object"}},"required":["tool"],"additionalProperties":true}}},"required":["steps"],"additionalProperties":false}""";

    private const string SnapshotDescription =
        "Return a compact accessibility-tree snapshot of the active agent-browser tab. Lines use role \"name\" [ref=N]; " +
        "interactive elements carry a ref you pass to click/type. Call this before any action, and take a new snapshot after " +
        "navigation or a DOM change: refs from a previous document are rejected as browser_ref_stale. Never guess coordinates. " +
        "Requires the desktop browser host; when it is not running the result is browser_host_unavailable.";

    private const string NavigateDescription =
        "Navigate the agent browser to an http(s) URL. The browser window opens on first use, so the user can watch. " +
        "Approval-gated. After it returns, call browser_snapshot before clicking or typing. Only http(s), loopback or public " +
        "addresses are accepted; metadata, private LAN, file:, data: and javascript: URLs are refused. " +
        "Requires the desktop browser host.";

    private const string TabsListDescription =
        "List the agent-browser tabs with their ids, urls, titles and active flag. Use browser_tab_open/select/close to change " +
        "them. Requires the desktop browser host.";

    private const string WaitDescription =
        "Wait up to timeout_ms for the active tab's body to contain text, or for its URL to contain url_contains. Returns " +
        "browser_wait_timeout when the condition does not appear; that is a normal outcome, not a broken page. This does not " +
        "replace a snapshot. Requires the desktop browser host.";

    private const string ScreenshotDescription =
        "Save a PNG screenshot of the active tab for the user (not for you: you cannot view images). Returns the absolute file " +
        "path and byte count. Use the accessibility snapshot for page understanding. Requires the desktop browser host.";

    private const string ClickDescription =
        "Click the element identified by a ref from the most recent browser_snapshot. Dispatches a real mouse event in the " +
        "agent browser. Approval-gated. Refs die on navigation and DOM changes: browser_ref_stale / browser_ref_unknown means " +
        "take a new snapshot. If the element is hidden, disabled, zero-sized, or covered, the result is " +
        "browser_element_not_actionable; wait or scroll instead of guessing coordinates. Requires the desktop browser host.";

    private const string TypeDescription =
        "Focus the element identified by a ref, replace its text, and verify the value. submit=true presses Enter afterwards. " +
        "Approval-gated. Refs come from browser_snapshot and are invalid after navigation. Requires the desktop browser host.";

    private const string PressDescription =
        "Press a key or chord in the active tab (optionally focusing a ref first). Named keys: Enter, Tab, Escape, Backspace, " +
        "Delete, ArrowUp/Down/Left/Right, Home, End, PageUp, PageDown, Space; or a single character; prefix modifiers with " +
        "Control, Shift, Alt, Meta, or ControlOrMeta (for example ControlOrMeta+A). Approval-gated. Requires the desktop browser host.";

    private const string ScrollDescription =
        "Send a wheel event at the center of a ref, or at the viewport center when no ref is given. delta_y positive scrolls " +
        "down. Approval-gated. Refs come from browser_snapshot. Requires the desktop browser host.";

    private const string HistoryDescription =
        "Move the active tab back, forward, or reload it. Approval-gated. Refs are invalidated because the document may change; " +
        "take a new snapshot afterwards. Requires the desktop browser host.";

    private const string TabOpenDescription =
        "Open a new agent-browser tab, optionally at an http(s) URL. Approval-gated. Returns the tab list and the new active " +
        "tab id. Requires the desktop browser host.";

    private const string TabSelectDescription =
        "Make an existing agent-browser tab active. Approval-gated. Refs belong to the tab that produced the snapshot, so take " +
        "a new snapshot after switching. Requires the desktop browser host.";

    private const string TabCloseDescription =
        "Close an agent-browser tab. Closing the last tab closes the browser window. Approval-gated. Requires the desktop " +
        "browser host.";

    private const string BatchDescription =
        "Run 1-20 click/type/press/scroll steps in order under one approval; stops at the first failure and returns every " +
        "step's outcome plus a fresh snapshot with new refs. navigation, tab changes and waiting are deliberately not batch " +
        "steps: navigate, snapshot, then batch the interactions. Requires the desktop browser host.";

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0) return;

        ToolRegistry.Register(
            SnapshotToolId,
            HandleSnapshotAsync,
            requiresApproval: false,
            description: SnapshotDescription,
            inputSchemaJson: SnapshotSchema,
            risk: "low",
            mutatesWorkspace: false,
            retrySafety: "safe");

        ToolRegistry.Register(
            TabsListToolId,
            HandleTabsListAsync,
            requiresApproval: false,
            description: TabsListDescription,
            inputSchemaJson: TabsListSchema,
            risk: "low",
            mutatesWorkspace: false,
            retrySafety: "safe");

        ToolRegistry.Register(
            WaitToolId,
            HandleWaitAsync,
            requiresApproval: false,
            description: WaitDescription,
            inputSchemaJson: WaitSchema,
            risk: "low",
            mutatesWorkspace: false,
            retrySafety: "safe");

        ToolRegistry.Register(
            ScreenshotToolId,
            HandleScreenshotAsync,
            requiresApproval: false,
            description: ScreenshotDescription,
            inputSchemaJson: ScreenshotSchema,
            risk: "low",
            mutatesWorkspace: false,
            retrySafety: "safe");

        ToolRegistry.Register(
            NavigateToolId,
            HandleNavigateAsync,
            requiresApproval: true,
            description: NavigateDescription,
            inputSchemaJson: NavigateSchema,
            risk: "high",
            mutatesWorkspace: false,
            retrySafety: "unsafe");

        ToolRegistry.Register(
            ClickToolId,
            HandleClickAsync,
            requiresApproval: true,
            description: ClickDescription,
            inputSchemaJson: ClickSchema,
            risk: "high",
            mutatesWorkspace: false,
            retrySafety: "unsafe");

        ToolRegistry.Register(
            TypeToolId,
            HandleTypeAsync,
            requiresApproval: true,
            description: TypeDescription,
            inputSchemaJson: TypeSchema,
            risk: "high",
            mutatesWorkspace: false,
            retrySafety: "unsafe");

        ToolRegistry.Register(
            PressToolId,
            HandlePressAsync,
            requiresApproval: true,
            description: PressDescription,
            inputSchemaJson: PressSchema,
            risk: "high",
            mutatesWorkspace: false,
            retrySafety: "unsafe");

        ToolRegistry.Register(
            ScrollToolId,
            HandleScrollAsync,
            requiresApproval: true,
            description: ScrollDescription,
            inputSchemaJson: ScrollSchema,
            risk: "high",
            mutatesWorkspace: false,
            retrySafety: "unsafe");

        ToolRegistry.Register(
            HistoryToolId,
            HandleHistoryAsync,
            requiresApproval: true,
            description: HistoryDescription,
            inputSchemaJson: HistorySchema,
            risk: "high",
            mutatesWorkspace: false,
            retrySafety: "unsafe");

        ToolRegistry.Register(
            TabOpenToolId,
            HandleTabOpenAsync,
            requiresApproval: true,
            description: TabOpenDescription,
            inputSchemaJson: TabOpenSchema,
            risk: "high",
            mutatesWorkspace: false,
            retrySafety: "unsafe");

        ToolRegistry.Register(
            TabSelectToolId,
            HandleTabSelectAsync,
            requiresApproval: true,
            description: TabSelectDescription,
            inputSchemaJson: TabIdSchema,
            risk: "high",
            mutatesWorkspace: false,
            retrySafety: "unsafe");

        ToolRegistry.Register(
            TabCloseToolId,
            HandleTabCloseAsync,
            requiresApproval: true,
            description: TabCloseDescription,
            inputSchemaJson: TabIdSchema,
            risk: "high",
            mutatesWorkspace: false,
            retrySafety: "unsafe");

        ToolRegistry.Register(
            BatchToolId,
            HandleBatchAsync,
            requiresApproval: true,
            description: BatchDescription,
            inputSchemaJson: BatchSchema,
            risk: "high",
            mutatesWorkspace: false,
            retrySafety: "unsafe");
    }

    private static async ValueTask<ToolCallResponse<JsonElement>> HandleSnapshotAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserSnapshotArgs);
        return await RunAsync(
            request,
            () => BrowserToolRuntime.Default.SnapshotAsync(request.SessionId, args, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ToolCallResponse<JsonElement>> HandleNavigateAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return NotApproved(request.ToolCallId);
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserNavigateArgs);
        return await RunAsync(
            request,
            () => BrowserToolRuntime.Default.NavigateAsync(request.SessionId, args, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ToolCallResponse<JsonElement>> HandleTabsListAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        return await RunAsync(
            request,
            () => BrowserToolRuntime.Default.TabsListAsync(request.SessionId, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ToolCallResponse<JsonElement>> HandleWaitAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserWaitArgs);
        return await RunAsync(
            request,
            () => BrowserToolRuntime.Default.WaitAsync(request.SessionId, args, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ToolCallResponse<JsonElement>> HandleScreenshotAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserScreenshotArgs);
        return await RunAsync(
            request,
            () => BrowserToolRuntime.Default.ScreenshotAsync(request.SessionId, args, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ToolCallResponse<JsonElement>> HandleClickAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return NotApproved(request.ToolCallId);
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserClickArgs);
        return await RunAsync(request, () => BrowserToolRuntime.Default.ClickAsync(request.SessionId, args, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ToolCallResponse<JsonElement>> HandleTypeAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return NotApproved(request.ToolCallId);
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserTypeArgs);
        return await RunAsync(request, () => BrowserToolRuntime.Default.TypeAsync(request.SessionId, args, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ToolCallResponse<JsonElement>> HandlePressAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return NotApproved(request.ToolCallId);
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserPressArgs);
        return await RunAsync(request, () => BrowserToolRuntime.Default.PressAsync(request.SessionId, args, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ToolCallResponse<JsonElement>> HandleScrollAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return NotApproved(request.ToolCallId);
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserScrollArgs);
        return await RunAsync(request, () => BrowserToolRuntime.Default.ScrollAsync(request.SessionId, args, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ToolCallResponse<JsonElement>> HandleHistoryAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return NotApproved(request.ToolCallId);
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserHistoryArgs);
        return await RunAsync(request, () => BrowserToolRuntime.Default.HistoryAsync(request.SessionId, args, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ToolCallResponse<JsonElement>> HandleTabOpenAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return NotApproved(request.ToolCallId);
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserTabOpenArgs);
        return await RunAsync(request, () => BrowserToolRuntime.Default.TabOpenAsync(request.SessionId, args, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ToolCallResponse<JsonElement>> HandleTabSelectAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return NotApproved(request.ToolCallId);
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserTabArgs);
        return await RunAsync(request, () => BrowserToolRuntime.Default.TabSelectAsync(request.SessionId, args, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ToolCallResponse<JsonElement>> HandleTabCloseAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return NotApproved(request.ToolCallId);
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserTabArgs);
        return await RunAsync(request, () => BrowserToolRuntime.Default.TabCloseAsync(request.SessionId, args, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ToolCallResponse<JsonElement>> HandleBatchAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return NotApproved(request.ToolCallId);
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserBatchArgs);
        return await RunAsync(request, () => BrowserToolRuntime.Default.BatchAsync(request.SessionId, args, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ToolCallResponse<JsonElement>> RunAsync(
        ToolCallRequest<JsonElement> request,
        Func<Task<BrowserToolResult>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await action().ConfigureAwait(false);
            return Success(request.ToolCallId, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Logger.Warn(exception, "browser tool {ToolId} failed unexpectedly", request.ToolId);
            return Success(request.ToolCallId, new BrowserToolResult
            {
                Ok = false,
                Code = "browser_tool_error",
                Message = exception.Message,
                Permanent = false,
            });
        }
    }

    private static ToolCallResponse<JsonElement> Success(long callId, BrowserToolResult result) => new()
    {
        CallId = callId,
        IsSuccess = true,
        Response = JsonSerializer.SerializeToElement(result, BrowserJsonContext.Default.BrowserToolResult),
    };

    private static ToolCallResponse<JsonElement> NotApproved(long callId) => new()
    {
        CallId = callId,
        IsSuccess = false,
        Response = JsonSerializer.SerializeToElement(NotApprovedResponse.MESSAGE, ToolCallJsonContext.Default.String),
    };

    private static T Deserialize<T>(JsonElement parameters, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : new()
    {
        if (parameters.ValueKind != JsonValueKind.Object) return new T();
        try
        {
            return parameters.Deserialize(typeInfo) ?? new T();
        }
        catch (JsonException)
        {
            return new T();
        }
    }
}
