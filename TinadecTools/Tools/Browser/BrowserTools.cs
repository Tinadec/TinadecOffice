using System.Text.Json;
using NLog;
using TinadecTools.Abstractions;

namespace TinadecTools.Tools.Browser;

/// <summary>
/// Registers the unified v2 browser tools (<c>builtin_electron</c> &amp; <c>ego_lite</c> drivers)
/// with TaskSpace isolation, human handoff/takeover, governed uploads/downloads, and single-turn
/// multi-step <c>browser_run</c> workflows.
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
    public const string HandoffToolId = "browser_handoff";
    public const string TakeoverToolId = "browser_takeover";
    public const string FinishToolId = "browser_finish";
    public const string DialogToolId = "browser_dialog";
    public const string ProfilesListToolId = "browser_profiles_list";
    public const string UploadToolId = "browser_upload";
    public const string DownloadSaveToolId = "browser_download_save";
    public const string RunToolId = "browser_run";

    public static readonly IReadOnlyList<string> AllToolIds =
    [
        SnapshotToolId,
        NavigateToolId,
        TabsListToolId,
        WaitToolId,
        ScreenshotToolId,
        ClickToolId,
        TypeToolId,
        PressToolId,
        ScrollToolId,
        HistoryToolId,
        TabOpenToolId,
        TabSelectToolId,
        TabCloseToolId,
        BatchToolId,
        HandoffToolId,
        TakeoverToolId,
        FinishToolId,
        DialogToolId,
        ProfilesListToolId,
        UploadToolId,
        DownloadSaveToolId,
        RunToolId,
    ];

    private const string DriverSchemaField = """
        "driver":{"type":"string","enum":["builtin_electron","ego_lite"]}
        """;

    private const string TaskSpaceSchemaField = """
        "task_space":{"type":"string","maxLength":120}
        """;

    private const string ProfileSchemaField = """
        "profile_id":{"type":"string","maxLength":120}
        """;

    private const string SnapshotSchema =
        "{\"type\":\"object\",\"properties\":{\"max_chars\":{\"type\":\"integer\",\"minimum\":2048,\"maximum\":64000},\"scope\":{\"type\":\"string\",\"enum\":[\"viewport\",\"full_page\",\"subtree\"]},\"root_ref\":{\"type\":\"integer\",\"minimum\":1}," +
        TaskSpaceSchemaField + "," + DriverSchemaField + "," + ProfileSchemaField + "},\"additionalProperties\":false}";

    private const string NavigateSchema =
        "{\"type\":\"object\",\"properties\":{\"url\":{\"type\":\"string\"},\"wait_ms\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":30000}," +
        TaskSpaceSchemaField + "," + DriverSchemaField + "," + ProfileSchemaField + "},\"required\":[\"url\"],\"additionalProperties\":false}";

    private const string TabsListSchema =
        "{\"type\":\"object\",\"properties\":{" + TaskSpaceSchemaField + "," + DriverSchemaField + "},\"additionalProperties\":false}";

    private const string WaitSchema =
        "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"},\"url_contains\":{\"type\":\"string\"},\"selector\":{\"type\":\"string\"},\"timeout_ms\":{\"type\":\"integer\",\"minimum\":100,\"maximum\":60000}," +
        TaskSpaceSchemaField + "," + DriverSchemaField + "},\"additionalProperties\":false}";

    private const string ScreenshotSchema =
        "{\"type\":\"object\",\"properties\":{\"full_page\":{\"type\":\"boolean\"}," +
        TaskSpaceSchemaField + "," + DriverSchemaField + "},\"additionalProperties\":false}";

    private const string ClickSchema =
        "{\"type\":\"object\",\"properties\":{\"ref\":{\"type\":\"integer\",\"minimum\":1},\"selector\":{\"type\":\"string\"},\"button\":{\"type\":\"string\",\"enum\":[\"left\",\"right\",\"middle\"]},\"click_count\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":2}," +
        TaskSpaceSchemaField + "," + DriverSchemaField + "},\"required\":[\"ref\"],\"additionalProperties\":false}";

    private const string TypeSchema =
        "{\"type\":\"object\",\"properties\":{\"ref\":{\"type\":\"integer\",\"minimum\":1},\"selector\":{\"type\":\"string\"},\"text\":{\"type\":\"string\"},\"submit\":{\"type\":\"boolean\"}," +
        TaskSpaceSchemaField + "," + DriverSchemaField + "},\"required\":[\"ref\"],\"additionalProperties\":false}";

    private const string PressSchema =
        "{\"type\":\"object\",\"properties\":{\"key\":{\"type\":\"string\"},\"ref\":{\"type\":\"integer\",\"minimum\":1},\"selector\":{\"type\":\"string\"}," +
        TaskSpaceSchemaField + "," + DriverSchemaField + "},\"required\":[\"key\"],\"additionalProperties\":false}";

    private const string ScrollSchema =
        "{\"type\":\"object\",\"properties\":{\"ref\":{\"type\":\"integer\",\"minimum\":1},\"selector\":{\"type\":\"string\"},\"delta_x\":{\"type\":\"number\"},\"delta_y\":{\"type\":\"number\"}," +
        TaskSpaceSchemaField + "," + DriverSchemaField + "},\"additionalProperties\":false}";

    private const string HistorySchema =
        "{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\",\"enum\":[\"back\",\"forward\",\"reload\"]}," +
        TaskSpaceSchemaField + "," + DriverSchemaField + "},\"required\":[\"action\"],\"additionalProperties\":false}";

    private const string TabOpenSchema =
        "{\"type\":\"object\",\"properties\":{\"url\":{\"type\":\"string\"}," +
        TaskSpaceSchemaField + "," + DriverSchemaField + "," + ProfileSchemaField + "},\"additionalProperties\":false}";

    private const string TabIdSchema =
        "{\"type\":\"object\",\"properties\":{\"tab_id\":{\"type\":\"string\"}," +
        TaskSpaceSchemaField + "," + DriverSchemaField + "},\"required\":[\"tab_id\"],\"additionalProperties\":false}";

    private const string BatchSchema =
        "{\"type\":\"object\",\"properties\":{\"steps\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":20,\"items\":{\"type\":\"object\",\"properties\":{\"tool\":{\"type\":\"string\",\"enum\":[\"browser_click\",\"browser_type\",\"browser_press\",\"browser_scroll\"]},\"args\":{\"type\":\"object\"}},\"required\":[\"tool\"],\"additionalProperties\":true}}," +
        TaskSpaceSchemaField + "," + DriverSchemaField + "},\"required\":[\"steps\"],\"additionalProperties\":false}";

    private const string HandoffSchema =
        "{\"type\":\"object\",\"properties\":{\"reason\":{\"type\":\"string\"},\"message\":{\"type\":\"string\"}," +
        TaskSpaceSchemaField + "," + DriverSchemaField + "," + ProfileSchemaField + "},\"additionalProperties\":false}";

    private const string TakeoverSchema =
        "{\"type\":\"object\",\"properties\":{\"space_id\":{\"type\":\"string\"},\"claim_user_space\":{\"type\":\"boolean\"}," +
        TaskSpaceSchemaField + "," + DriverSchemaField + "," + ProfileSchemaField + "},\"additionalProperties\":false}";

    private const string FinishSchema =
        "{\"type\":\"object\",\"properties\":{\"keep\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"space_id\":{\"type\":\"string\"}," +
        TaskSpaceSchemaField + "," + DriverSchemaField + "},\"additionalProperties\":false}";

    private const string DialogSchema =
        "{\"type\":\"object\",\"properties\":{\"accept\":{\"type\":\"boolean\"},\"prompt_text\":{\"type\":\"string\",\"maxLength\":4096}," +
        TaskSpaceSchemaField + "," + DriverSchemaField + "},\"required\":[\"accept\"],\"additionalProperties\":false}";

    private const string ProfilesListSchema =
        """{"type":"object","properties":{"driver":{"type":"string","enum":["builtin_electron","ego_lite","all"]}},"additionalProperties":false}""";

    private const string UploadSchema =
        "{\"type\":\"object\",\"properties\":{\"ref\":{\"type\":\"integer\",\"minimum\":1},\"selector\":{\"type\":\"string\"},\"path\":{\"type\":\"string\"},\"paths\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}," +
        TaskSpaceSchemaField + "," + DriverSchemaField + "},\"additionalProperties\":false}";

    private const string DownloadSaveSchema =
        "{\"type\":\"object\",\"properties\":{\"save_path\":{\"type\":\"string\"},\"url\":{\"type\":\"string\"},\"ref\":{\"type\":\"integer\",\"minimum\":1},\"selector\":{\"type\":\"string\"},\"timeout_ms\":{\"type\":\"integer\",\"minimum\":500,\"maximum\":120000}," +
        TaskSpaceSchemaField + "," + DriverSchemaField + "},\"required\":[\"save_path\"],\"additionalProperties\":false}";

    private const string RunStepItemSchema =
        """{"type":"object","properties":{"action":{"type":"string","enum":["goto","fill","click","select_option","press","scroll","wait_for_selector","wait_for_url","wait_for_text","handle_dialog","extract"]},"url":{"type":"string"},"wait_ms":{"type":"integer","minimum":0,"maximum":30000},"ref":{"type":"integer","minimum":1},"selector":{"type":"string"},"text":{"type":"string"},"submit":{"type":"boolean"},"button":{"type":"string","enum":["left","right","middle"]},"click_count":{"type":"integer","minimum":1,"maximum":2},"value":{"type":"string"},"label":{"type":"string"},"index":{"type":"integer","minimum":0},"values":{"type":"array","items":{"type":"string"}},"key":{"type":"string"},"delta_x":{"type":"number"},"delta_y":{"type":"number"},"state":{"type":"string","enum":["attached","visible","hidden","detached"]},"url_contains":{"type":"string"},"timeout_ms":{"type":"integer","minimum":100,"maximum":60000},"accept":{"type":"boolean"},"prompt_text":{"type":"string","maxLength":4096},"attribute":{"type":"string"},"multiple":{"type":"boolean"},"limit":{"type":"integer","minimum":1,"maximum":100},"name":{"type":"string"}},"required":["action"],"additionalProperties":false}""";

    private const string RunSchema =
        "{\"type\":\"object\",\"properties\":{\"steps\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":30,\"items\":" + RunStepItemSchema +
        "},\"include_snapshot\":{\"type\":\"boolean\"},\"snapshot_scope\":{\"type\":\"string\",\"enum\":[\"viewport\",\"full_page\",\"subtree\"]},\"max_snapshot_chars\":{\"type\":\"integer\",\"minimum\":2048,\"maximum\":64000},\"include_screenshot\":{\"type\":\"boolean\"},\"full_page_screenshot\":{\"type\":\"boolean\"}," +
        TaskSpaceSchemaField + "," + DriverSchemaField + "," + ProfileSchemaField + "},\"required\":[\"steps\"],\"additionalProperties\":false}";

    private const string SnapshotDescription =
        "Return a compact accessibility-tree snapshot of the active browser tab (scope: viewport, full_page, or subtree with root_ref). " +
        "Lines use role \"name\" [ref=N] with verified unique loc=role:... / loc=css:... fallbacks, including across same-origin iframes and OOPIFs. " +
        "Call this before acting, and take a new snapshot after navigation or DOM changes.";

    private const string NavigateDescription =
        "Navigate the active browser TaskSpace to an http(s) URL (builtin_electron or external ego_lite driver). Approval-gated. " +
        "Only http(s), loopback or public addresses are accepted; metadata, private LAN (unless enabled), file:, data: and javascript: URLs are refused.";

    private const string TabsListDescription =
        "List tabs in the active browser TaskSpace with their ids, urls, titles and active flag.";

    private const string WaitDescription =
        "Wait up to timeout_ms for the active tab's body to contain text, for its URL to contain url_contains, or for selector to match. " +
        "Returns browser_wait_timeout when the condition does not appear in time.";

    private const string ScreenshotDescription =
        "Capture a PNG screenshot of the active browser tab. Returns screenshot_path (and path) plus byte count; vision-capable models " +
        "receive the captured PNG image directly as multimodal visual input alongside the tool result. Prefer browser_snapshot for ordinary DOM semantics, " +
        "and use browser_screenshot for visual layout verification, canvas, charts, or rich editors.";

    private const string ClickDescription =
        "Click the element identified by a ref from browser_snapshot (or a verified loc=/CSS selector). Approval-gated. " +
        "Returns an action receipt reporting opened_tabs, pending_dialog, and console_errors.";

    private const string TypeDescription =
        "Focus the element identified by a ref (or selector), replace its text, and verify the value. submit=true presses Enter afterwards. Approval-gated.";

    private const string PressDescription =
        "Press a key or chord in the active tab (optionally focusing a ref first). Named keys: Enter, Tab, Escape, Backspace, Delete, " +
        "ArrowUp/Down/Left/Right, Home, End, PageUp, PageDown, Space; or a single character with Control/Shift/Alt/Meta/ControlOrMeta. Approval-gated.";

    private const string ScrollDescription =
        "Send a wheel event at the center of a ref, or at the viewport center when no ref is given. delta_y positive scrolls down. Approval-gated.";

    private const string HistoryDescription =
        "Move the active tab back, forward, or reload it. Approval-gated. Invalidates refs because the document may change.";

    private const string TabOpenDescription =
        "Open a new browser tab in the current TaskSpace, optionally at an http(s) URL. Approval-gated.";

    private const string TabSelectDescription =
        "Make an existing browser tab active in the current TaskSpace. Approval-gated.";

    private const string TabCloseDescription =
        "Close a browser tab in the current TaskSpace. Approval-gated.";

    private const string BatchDescription =
        "Run 1-20 click/type/press/scroll steps in order under one approval; stops at the first failure and returns a fresh snapshot.";

    private const string HandoffDescription =
        "Hand off the current browser TaskSpace to user control for login, 2FA, CAPTCHA, or manual verification. Locks agent browser writes " +
        "and returns space_id plus browser_handoff_requested; stop and wait for the user to confirm before calling browser_takeover.";

    private const string TakeoverDescription =
        "Resume agent control of a handed-off browser TaskSpace after the user confirms completion. Approval-gated. " +
        "Refuses resuming a finished/ended TaskSpace (browser_task_space_finished / EGO_TASK_SPACE_INACTIVE).";

    private const string FinishDescription =
        "Finish the current browser TaskSpace, keeping only tab IDs listed in keep (defaults to []) and closing intermediate clutter. Approval-gated.";

    private const string DialogDescription =
        "Handle an open JavaScript alert, confirm, or prompt dialog on the active TaskSpace (accept=true/false, optional prompt_text). Approval-gated.";

    private const string ProfilesListDescription =
        "List available browser profiles across builtin_electron and external ego_lite drivers.";

    private const string UploadDescription =
        "Set workspace-governed local files on a file input identified by ref or selector. Refuses paths outside allowed workspace roots. Approval-gated.";

    private const string DownloadSaveDescription =
        "Trigger or capture a browser download and save it to save_path inside the writable workspace, enforcing BrowserNavigationPolicy and workspace boundaries. Approval-gated.";

    private const string RunDescription =
        "Execute a governed 1-30 step declarative browser workflow (goto, fill, click, select_option, press, scroll, wait_for_selector, " +
        "wait_for_url, wait_for_text, handle_dialog, extract) under ONE approval and ONE turn, enforcing BrowserNavigationPolicy on every goto step " +
        "and ending with a compact snapshot plus optional multimodal screenshot.";

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
            ProfilesListToolId,
            HandleProfilesListAsync,
            requiresApproval: false,
            description: ProfilesListDescription,
            inputSchemaJson: ProfilesListSchema,
            risk: "low",
            mutatesWorkspace: false,
            retrySafety: "safe");

        ToolRegistry.Register(
            HandoffToolId,
            HandleHandoffAsync,
            requiresApproval: false,
            description: HandoffDescription,
            inputSchemaJson: HandoffSchema,
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

        ToolRegistry.Register(
            TakeoverToolId,
            HandleTakeoverAsync,
            requiresApproval: true,
            description: TakeoverDescription,
            inputSchemaJson: TakeoverSchema,
            risk: "high",
            mutatesWorkspace: false,
            retrySafety: "unsafe");

        ToolRegistry.Register(
            FinishToolId,
            HandleFinishAsync,
            requiresApproval: true,
            description: FinishDescription,
            inputSchemaJson: FinishSchema,
            risk: "high",
            mutatesWorkspace: false,
            retrySafety: "unsafe");

        ToolRegistry.Register(
            DialogToolId,
            HandleDialogAsync,
            requiresApproval: true,
            description: DialogDescription,
            inputSchemaJson: DialogSchema,
            risk: "high",
            mutatesWorkspace: false,
            retrySafety: "unsafe");

        ToolRegistry.Register(
            UploadToolId,
            HandleUploadAsync,
            requiresApproval: true,
            description: UploadDescription,
            inputSchemaJson: UploadSchema,
            risk: "high",
            mutatesWorkspace: false,
            retrySafety: "unsafe");

        ToolRegistry.Register(
            DownloadSaveToolId,
            HandleDownloadSaveAsync,
            requiresApproval: true,
            description: DownloadSaveDescription,
            inputSchemaJson: DownloadSaveSchema,
            risk: "high",
            mutatesWorkspace: true,
            retrySafety: "unsafe");

        ToolRegistry.Register(
            RunToolId,
            HandleRunAsync,
            requiresApproval: true,
            description: RunDescription,
            inputSchemaJson: RunSchema,
            risk: "high",
            mutatesWorkspace: false,
            retrySafety: "unsafe");
    }

    private static ValueTask<ToolCallResponse<JsonElement>> HandleSnapshotAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserSnapshotArgs);
        return DispatchToConnectorAsync(
            request, args.TaskSpace, args.Driver, args.ProfileId,
            (connector, ctx) => connector.SnapshotAsync(ctx, args, cancellationToken),
            cancellationToken);
    }

    private static ValueTask<ToolCallResponse<JsonElement>> HandleNavigateAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return ValueTask.FromResult(NotApproved(request.ToolCallId));
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserNavigateArgs);
        return DispatchToConnectorAsync(
            request, args.TaskSpace, args.Driver, args.ProfileId,
            (connector, ctx) => connector.NavigateAsync(ctx, args, cancellationToken),
            cancellationToken);
    }

    private static ValueTask<ToolCallResponse<JsonElement>> HandleTabsListAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserTabsListArgs);
        return DispatchToConnectorAsync(
            request, args.TaskSpace, args.Driver, null,
            (connector, ctx) => connector.TabsListAsync(ctx, cancellationToken),
            cancellationToken);
    }

    private static ValueTask<ToolCallResponse<JsonElement>> HandleWaitAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserWaitArgs);
        return DispatchToConnectorAsync(
            request, args.TaskSpace, args.Driver, null,
            (connector, ctx) => connector.WaitAsync(ctx, args, cancellationToken),
            cancellationToken);
    }

    private static ValueTask<ToolCallResponse<JsonElement>> HandleScreenshotAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserScreenshotArgs);
        return DispatchToConnectorAsync(
            request, args.TaskSpace, args.Driver, null,
            (connector, ctx) => connector.ScreenshotAsync(ctx, args, cancellationToken),
            cancellationToken);
    }

    private static ValueTask<ToolCallResponse<JsonElement>> HandleClickAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return ValueTask.FromResult(NotApproved(request.ToolCallId));
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserClickArgs);
        return DispatchToConnectorAsync(
            request, args.TaskSpace, args.Driver, null,
            (connector, ctx) => connector.ClickAsync(ctx, args, cancellationToken),
            cancellationToken);
    }

    private static ValueTask<ToolCallResponse<JsonElement>> HandleTypeAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return ValueTask.FromResult(NotApproved(request.ToolCallId));
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserTypeArgs);
        return DispatchToConnectorAsync(
            request, args.TaskSpace, args.Driver, null,
            (connector, ctx) => connector.TypeAsync(ctx, args, cancellationToken),
            cancellationToken);
    }

    private static ValueTask<ToolCallResponse<JsonElement>> HandlePressAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return ValueTask.FromResult(NotApproved(request.ToolCallId));
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserPressArgs);
        return DispatchToConnectorAsync(
            request, args.TaskSpace, args.Driver, null,
            (connector, ctx) => connector.PressAsync(ctx, args, cancellationToken),
            cancellationToken);
    }

    private static ValueTask<ToolCallResponse<JsonElement>> HandleScrollAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return ValueTask.FromResult(NotApproved(request.ToolCallId));
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserScrollArgs);
        return DispatchToConnectorAsync(
            request, args.TaskSpace, args.Driver, null,
            (connector, ctx) => connector.ScrollAsync(ctx, args, cancellationToken),
            cancellationToken);
    }

    private static ValueTask<ToolCallResponse<JsonElement>> HandleHistoryAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return ValueTask.FromResult(NotApproved(request.ToolCallId));
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserHistoryArgs);
        return DispatchToConnectorAsync(
            request, args.TaskSpace, args.Driver, null,
            (connector, ctx) => connector.HistoryAsync(ctx, args, cancellationToken),
            cancellationToken);
    }

    private static ValueTask<ToolCallResponse<JsonElement>> HandleTabOpenAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return ValueTask.FromResult(NotApproved(request.ToolCallId));
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserTabOpenArgs);
        return DispatchToConnectorAsync(
            request, args.TaskSpace, args.Driver, args.ProfileId,
            (connector, ctx) => connector.TabOpenAsync(ctx, args, cancellationToken),
            cancellationToken);
    }

    private static ValueTask<ToolCallResponse<JsonElement>> HandleTabSelectAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return ValueTask.FromResult(NotApproved(request.ToolCallId));
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserTabArgs);
        return DispatchToConnectorAsync(
            request, args.TaskSpace, args.Driver, null,
            (connector, ctx) => connector.TabSelectAsync(ctx, args, cancellationToken),
            cancellationToken);
    }

    private static ValueTask<ToolCallResponse<JsonElement>> HandleTabCloseAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return ValueTask.FromResult(NotApproved(request.ToolCallId));
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserTabArgs);
        return DispatchToConnectorAsync(
            request, args.TaskSpace, args.Driver, null,
            (connector, ctx) => connector.TabCloseAsync(ctx, args, cancellationToken),
            cancellationToken);
    }

    private static ValueTask<ToolCallResponse<JsonElement>> HandleBatchAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return ValueTask.FromResult(NotApproved(request.ToolCallId));
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserBatchArgs);
        return DispatchToConnectorAsync(
            request, args.TaskSpace, args.Driver, null,
            (connector, ctx) => connector.BatchAsync(ctx, args, cancellationToken),
            cancellationToken);
    }

    private static ValueTask<ToolCallResponse<JsonElement>> HandleHandoffAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserHandoffArgs);
        return DispatchToConnectorAsync(
            request, args.TaskSpace, args.Driver, args.ProfileId,
            (connector, ctx) => connector.HandOffAsync(ctx, args, cancellationToken),
            cancellationToken);
    }

    private static ValueTask<ToolCallResponse<JsonElement>> HandleTakeoverAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return ValueTask.FromResult(NotApproved(request.ToolCallId));
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserTakeoverArgs);
        return DispatchToConnectorAsync(
            request, args.TaskSpace, args.Driver, args.ProfileId,
            (connector, ctx) => connector.TakeOverAsync(ctx, args, cancellationToken),
            cancellationToken);
    }

    private static ValueTask<ToolCallResponse<JsonElement>> HandleFinishAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return ValueTask.FromResult(NotApproved(request.ToolCallId));
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserFinishArgs);
        return DispatchToConnectorAsync(
            request, args.TaskSpace, args.Driver, null,
            (connector, ctx) => connector.FinishAsync(ctx, args, cancellationToken),
            cancellationToken);
    }

    private static ValueTask<ToolCallResponse<JsonElement>> HandleDialogAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return ValueTask.FromResult(NotApproved(request.ToolCallId));
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserDialogArgs);
        return DispatchToConnectorAsync(
            request, args.TaskSpace, args.Driver, null,
            (connector, ctx) => connector.DialogAsync(ctx, args, cancellationToken),
            cancellationToken);
    }

    private static ValueTask<ToolCallResponse<JsonElement>> HandleProfilesListAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserProfilesListArgs);
        var context = BrowserConnectorContext.Resolve(request.SessionId, request.ExecutionContext);
        return RunAsync(
            request,
            () => BrowserConnectorRouter.Default.ProfilesListAsync(context, args, cancellationToken),
            cancellationToken);
    }

    private static ValueTask<ToolCallResponse<JsonElement>> HandleUploadAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return ValueTask.FromResult(NotApproved(request.ToolCallId));
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserUploadArgs);
        return DispatchToConnectorAsync(
            request, args.TaskSpace, args.Driver, null,
            (connector, ctx) => connector.UploadAsync(ctx, args, cancellationToken),
            cancellationToken);
    }

    private static ValueTask<ToolCallResponse<JsonElement>> HandleDownloadSaveAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return ValueTask.FromResult(NotApproved(request.ToolCallId));
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserDownloadSaveArgs);
        return DispatchToConnectorAsync(
            request, args.TaskSpace, args.Driver, null,
            (connector, ctx) => connector.DownloadSaveAsync(ctx, args, cancellationToken),
            cancellationToken);
    }

    private static ValueTask<ToolCallResponse<JsonElement>> HandleRunAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return ValueTask.FromResult(NotApproved(request.ToolCallId));
        var args = Deserialize(request.Params, BrowserJsonContext.Default.BrowserRunArgs);
        return DispatchToConnectorAsync(
            request, args.TaskSpace, args.Driver, args.ProfileId,
            (connector, ctx) => connector.RunAsync(ctx, args, cancellationToken),
            cancellationToken);
    }

    private static ValueTask<ToolCallResponse<JsonElement>> DispatchToConnectorAsync(
        ToolCallRequest<JsonElement> request,
        string? taskSpaceOverride,
        string? driverOverride,
        string? profileIdOverride,
        Func<IBrowserConnector, BrowserConnectorContext, Task<BrowserToolResult>> invoke,
        CancellationToken cancellationToken)
    {
        var context = BrowserConnectorContext.Resolve(
            request.SessionId,
            request.ExecutionContext,
            taskSpaceOverride,
            driverOverride,
            profileIdOverride);

        if (!BrowserConnectorRouter.Default.TrySelectConnector(context, out var connector, out var error))
        {
            return ValueTask.FromResult(Success(request.ToolCallId, error!));
        }

        return RunAsync(request, () => invoke(connector, context), cancellationToken);
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
