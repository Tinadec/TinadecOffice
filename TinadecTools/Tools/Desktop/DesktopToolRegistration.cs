using System.Text.Json;
using NLog;
using TinadecTools.Abstractions;

namespace TinadecTools.Tools.Desktop;

/// <summary>
/// Registers the desktop computer-use tools by hand (the same pattern as the browser tools:
/// the raw request is needed for nothing here, but the registration metadata - approval,
/// risk, retry safety - is the governance surface Core reads, so it is spelled out per tool).
///
/// Read tools (list/screenshot/read_text) are approval-free like browser_snapshot; every
/// action tool (click/type/press_key) is approval-gated, high risk and unsafe to retry.
/// </summary>
public static class DesktopToolRegistration
{
    public const string ListWindowsToolId = "desktop_list_windows";
    public const string ScreenshotToolId = "desktop_screenshot";
    public const string ReadTextToolId = "desktop_read_text";
    public const string ClickToolId = "desktop_click";
    public const string TypeToolId = "desktop_type";
    public const string PressKeyToolId = "desktop_press_key";
    public const string LaunchAppToolId = "desktop_launch_app";

    private const string ListWindowsSchema =
        """{"type":"object","properties":{"limit":{"type":"integer","minimum":1,"maximum":200},"include_all":{"type":"boolean"},"mode":{"type":"string","enum":["fast","silent","isolated"]}},"additionalProperties":false}""";

    private const string ScreenshotSchema =
        """{"type":"object","properties":{"window_id":{"type":"integer"},"mode":{"type":"string","enum":["fast","silent","isolated"]}},"additionalProperties":false}""";

    private const string ReadTextSchema =
        """{"type":"object","properties":{"window_id":{"type":"integer"},"max_chars":{"type":"integer","minimum":1,"maximum":16384},"mode":{"type":"string","enum":["fast","silent","isolated"]}},"required":["window_id"],"additionalProperties":false}""";

    private const string ClickSchema =
        """{"type":"object","properties":{"x":{"type":"integer"},"y":{"type":"integer"},"window_id":{"type":"integer"},"button":{"type":"string","enum":["left","right","middle"]},"count":{"type":"integer","minimum":1,"maximum":3},"mode":{"type":"string","enum":["fast","silent","isolated"]}},"required":["x","y"],"additionalProperties":false}""";

    private const string TypeSchema =
        """{"type":"object","properties":{"text":{"type":"string","minLength":1,"maxLength":4096},"window_id":{"type":"integer"},"x":{"type":"integer"},"y":{"type":"integer"},"mode":{"type":"string","enum":["fast","silent","isolated"]}},"required":["text"],"additionalProperties":false}""";

    private const string PressKeySchema =
        """{"type":"object","properties":{"keys":{"type":"string","minLength":1,"maxLength":64},"window_id":{"type":"integer"},"mode":{"type":"string","enum":["fast","silent","isolated"]}},"required":["keys"],"additionalProperties":false}""";

    private const string LaunchAppSchema =
        """{"type":"object","properties":{"command":{"type":"string","minLength":1,"maxLength":1024},"arguments":{"type":"string","maxLength":4096},"working_directory":{"type":"string","maxLength":1024}},"required":["command"],"additionalProperties":false}""";

    private const string ModeParameterDoc =
        "mode selects how the input is delivered: 'fast' (default) focuses the target window and sends real keyboard/mouse "
        + "input - it will interrupt the user but every application responds; 'silent' writes messages directly to a target "
        + "window on the user's desktop without changing focus or moving the cursor, but only windows that process posted "
        + "messages (standard Win32 controls do; many games and custom-drawn apps do not) respond; 'isolated' targets a "
        + "window on the hidden TinadecIsolated desktop (see desktop_launch_app) with the same message-direct mechanics, so "
        + "the user never sees the window at all. Real keyboard/mouse input cannot reach the isolated desktop - measured: "
        + "SendInput is rejected from a non-input desktop. The effective default comes from "
        + "TINADEC_HOME/config/computer-use.json (default_mode) when the argument is omitted. Never downgrade fast to silent "
        + "or isolated on your own: ask the user when a silent action fails.";

    private const string ListWindowsDescription =
        "List the top-level windows on the user's desktop, newest Z order first: window_id, pid, process name, title, class "
        + "name, bounds, foreground and minimized flags. window_id is the handle you pass to the other desktop tools. By "
        + "default only visible, titled, uncloaked windows are returned. Windows only.";

    private const string ScreenshotDescription =
        "Save a PNG screenshot for the user. With window_id the capture is that window only (works even when the window is "
        + "occluded; a minimized window fails with desktop_window_minimized); without it the whole virtual screen is "
        + "captured. Returns the absolute path, byte count and dimensions. The picture is for the user: unless visual input "
        + "is explicitly available to you, you cannot see it - use desktop_read_text and window titles to understand "
        + "content, and say so instead of guessing. Windows only.";

    private const string ReadTextDescription =
        "Read the text of a window and its child controls (WM_GETTEXT, so edit fields and static texts come back as "
        + "strings, bounded by max_chars). Use it to verify what desktop_type wrote or to read a dialog's content. "
        + "Read-only and focus-free. Windows only.";

    private const string ClickDescription =
        "Click at a position. With window_id, x/y are client-relative coordinates of that window (origin at its top-left "
        + "client corner); without it they are virtual-screen absolute coordinates (negative is valid for monitors left of "
        + "the primary). Approval-gated. " + ModeParameterDoc + " Write actions are never retried automatically. Windows only.";

    private const string TypeDescription =
        "Type literal text (up to 4096 characters; Unicode is sent as-is, no IME composition). In fast mode the target "
        + "window is focused first, so the user sees the typing; in silent mode the characters are posted as WM_CHAR to "
        + "the control at x/y when given, else to the first edit-class child. Approval-gated. " + ModeParameterDoc
        + " Windows only.";

    private const string PressKeyDescription =
        "Press a key or chord such as 'Enter', 'ctrl+a' or 'alt+f4'. Named keys: Enter, Tab, Escape, Backspace, Delete, "
        + "Space, Up, Down, Left, Right, Home, End, PageUp, PageDown, F1-F12; modifiers ctrl/shift/alt/win. In silent and "
        + "isolated modes only single keys (no chords) can be delivered, and window_id is required. Approval-gated. "
        + ModeParameterDoc + " Windows only.";

    private const string LaunchAppDescription =
        "Launch a process onto the hidden TinadecIsolated desktop and return its pid. This is the only way windows get "
        + "there: apps cannot move onto the isolated desktop after starting. The user never sees these windows; inspect "
        + "them with desktop_list_windows/desktop_screenshot/desktop_read_text (mode 'isolated') and drive them with "
        + "desktop_click/desktop_type (mode 'isolated'). Measured boundaries: the isolated desktop renders and captures "
        + "fine (DWM composes it), the clipboard is shared with the user's desktop, but real keyboard/mouse input cannot "
        + "reach it and GPU swapchains report occluded Presents. Approval-gated. Windows only.";

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0) return;

        ToolRegistry.Register(
            ListWindowsToolId,
            HandleListWindowsAsync,
            requiresApproval: false,
            description: ListWindowsDescription,
            inputSchemaJson: ListWindowsSchema,
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
            ReadTextToolId,
            HandleReadTextAsync,
            requiresApproval: false,
            description: ReadTextDescription,
            inputSchemaJson: ReadTextSchema,
            risk: "low",
            mutatesWorkspace: false,
            retrySafety: "safe");

        ToolRegistry.Register(
            ClickToolId,
            HandleClickAsync,
            requiresApproval: true,
            description: ClickDescription,
            inputSchemaJson: ClickSchema,
            risk: "high",
            mutatesWorkspace: false,
            retrySafety: "unsafe",
            confirmationFields: ["window_id", "x", "y", "mode"]);

        ToolRegistry.Register(
            TypeToolId,
            HandleTypeAsync,
            requiresApproval: true,
            description: TypeDescription,
            inputSchemaJson: TypeSchema,
            risk: "high",
            mutatesWorkspace: false,
            retrySafety: "unsafe",
            confirmationFields: ["window_id", "text", "mode"]);

        ToolRegistry.Register(
            PressKeyToolId,
            HandlePressKeyAsync,
            requiresApproval: true,
            description: PressKeyDescription,
            inputSchemaJson: PressKeySchema,
            risk: "high",
            mutatesWorkspace: false,
            retrySafety: "unsafe",
            confirmationFields: ["window_id", "keys", "mode"]);

        ToolRegistry.Register(
            LaunchAppToolId,
            HandleLaunchAppAsync,
            requiresApproval: true,
            description: LaunchAppDescription,
            inputSchemaJson: LaunchAppSchema,
            risk: "high",
            mutatesWorkspace: false,
            retrySafety: "unsafe",
            confirmationFields: ["command", "arguments"]);
    }

    private static async ValueTask<ToolCallResponse<JsonElement>> HandleListWindowsAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        var args = Deserialize(request.Params, DesktopJsonContext.Default.DesktopListWindowsArgs);
        return await RunAsync(request, () => Task.FromResult(Runtime().ListWindows(args)), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ToolCallResponse<JsonElement>> HandleScreenshotAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        var args = Deserialize(request.Params, DesktopJsonContext.Default.DesktopScreenshotArgs);
        return await RunAsync(request, () => Task.FromResult(Runtime().Screenshot(args)), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ToolCallResponse<JsonElement>> HandleReadTextAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        var args = Deserialize(request.Params, DesktopJsonContext.Default.DesktopReadTextArgs);
        return await RunAsync(request, () => Task.FromResult(Runtime().ReadText(args)), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ToolCallResponse<JsonElement>> HandleClickAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return NotApproved(request.ToolCallId);
        var args = Deserialize(request.Params, DesktopJsonContext.Default.DesktopClickArgs);
        return await RunAsync(request, () => Runtime().ClickAsync(args), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ToolCallResponse<JsonElement>> HandleTypeAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return NotApproved(request.ToolCallId);
        var args = Deserialize(request.Params, DesktopJsonContext.Default.DesktopTypeArgs);
        return await RunAsync(request, () => Runtime().TypeAsync(args), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ToolCallResponse<JsonElement>> HandlePressKeyAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return NotApproved(request.ToolCallId);
        var args = Deserialize(request.Params, DesktopJsonContext.Default.DesktopPressKeyArgs);
        return await RunAsync(request, () => Runtime().PressKeyAsync(args), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ToolCallResponse<JsonElement>> HandleLaunchAppAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken)
    {
        if (!request.Approved) return NotApproved(request.ToolCallId);
        var args = Deserialize(request.Params, DesktopJsonContext.Default.DesktopLaunchArgs);
        return await RunAsync(request, () => Task.FromResult(Runtime().LaunchApp(args)), cancellationToken).ConfigureAwait(false);
    }

    private static DesktopToolRuntime Runtime() => DesktopToolRuntime.Default;

    private static async ValueTask<ToolCallResponse<JsonElement>> RunAsync(
        ToolCallRequest<JsonElement> request,
        Func<Task<DesktopToolResult>> action,
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
        catch (DesktopToolRuntime.DesktopConfigurationException exception)
        {
            Logger.Warn(exception, "desktop tool {ToolId} has a broken mode configuration", request.ToolId);
            return Success(request.ToolCallId, new DesktopToolResult
            {
                Ok = false,
                Code = "desktop_config_invalid",
                Message = exception.Message,
                Permanent = true,
            });
        }
        catch (Exception exception)
        {
            Logger.Warn(exception, "desktop tool {ToolId} failed unexpectedly", request.ToolId);
            return Success(request.ToolCallId, new DesktopToolResult
            {
                Ok = false,
                Code = "desktop_tool_error",
                Message = exception.Message,
                Permanent = false,
            });
        }
    }

    private static ToolCallResponse<JsonElement> Success(long callId, DesktopToolResult result) => new()
    {
        CallId = callId,
        IsSuccess = true,
        Response = JsonSerializer.SerializeToElement(result, DesktopJsonContext.Default.DesktopToolResult),
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
