using System.Text.Json;
using NLog;
using TinadecTools.Runtime;

namespace TinadecTools.Tools.Desktop;

/// <summary>
/// Desktop computer-use runtime with two user-selectable modes:
///
/// - fast (default): real input via SendInput after focusing the target window, plus
///   GDI screen/window capture. Steals focus and moves the user's cursor on purpose:
///   every application responds, and the user can always take over by grabbing the
///   physical keyboard or mouse.
/// - silent: best-effort background operation. Capture stays PrintWindow (occlusion-safe)
///   and input becomes message-direct writes (PostMessage WM_*) to the target window,
///   so nothing is ever injected into the global input stream: no focus change, no
///   cursor movement. The price is application compatibility - only windows that
///   process posted messages (standard Win32 controls do) respond; when they do not,
///   the call fails with a stable code instead of pretending success.
///
/// Mode resolution order per call: the `mode` argument, the frozen execution context
/// setting `desktop.mode`, the TINADEC_COMPUTER_USE_MODE environment variable, the
/// TINADEC_HOME/config/computer-use.json `default_mode` key, and finally `fast`.
/// A broken config fails closed (desktop_config_invalid) rather than silently
/// upgrading silent to fast.
/// </summary>
internal sealed class DesktopToolRuntime
{
    public const string FastMode = "fast";
    public const string SilentMode = "silent";
    public const string IsolatedMode = "isolated";

    /// <summary>
    /// Long-edge cap for the model-facing context copy of a capture. Beyond keeping the payload
    /// small (a 2560×1600 full-screen PNG is ~1.4 MB raw), 1568 matches the resolution vision
    /// models keep after their own downscaling, so detail is not lost by fitting the budget.
    /// </summary>
    internal const int MaxContextImageEdge = 1568;

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly SemaphoreSlim InputGate = new(1, 1);

    /// <summary>
    /// A mode configuration that exists but is malformed (bad JSON, wrong type, unknown value).
    /// Deliberately an error rather than a silent fallback: a corrupt 'silent' must never
    /// quietly become the more intrusive 'fast'.
    /// </summary>
    internal sealed class DesktopConfigurationException : InvalidOperationException
    {
        public DesktopConfigurationException(string message) : base(message)
        {
        }
    }

    private readonly Func<string?> _homeProvider;
    private long _screenshotCounter;

    public static DesktopToolRuntime Default { get; } = new();

    public DesktopToolRuntime() : this(() => Environment.GetEnvironmentVariable("TINADEC_HOME"))
    {
    }

    internal DesktopToolRuntime(Func<string?> homeProvider)
    {
        _homeProvider = homeProvider;
    }

    // ── read tools ─────────────────────────────────────────────────────────────

    public DesktopToolResult ListWindows(DesktopListWindowsArgs args)
    {
        if (!OperatingSystem.IsWindows()) return UnsupportedPlatform();
        var limit = Math.Clamp(args.Limit ?? 50, 1, 200);
        if (args.Mode == IsolatedMode)
        {
            var rows = DesktopWin32.RunOnIsolatedDesktop(() => DesktopWin32.ListTopLevelWindows(args.IncludeAll == true, limit), out var error);
            if (error is not null) return Fail("desktop_isolated_failed", error, permanent: false);
            return new DesktopToolResult
            {
                Ok = true,
                Mode = IsolatedMode,
                Count = rows.Count,
                Windows = rows.Select(ToWindowInfo).ToArray(),
            };
        }
        using var dpi = DesktopWin32.PerMonitorDpiScope.Enter();
        var rowsDefault = DesktopWin32.ListTopLevelWindows(args.IncludeAll == true, limit);
        return new DesktopToolResult
        {
            Ok = true,
            Count = rowsDefault.Count,
            Windows = rowsDefault.Select(ToWindowInfo).ToArray(),
        };
    }

    public DesktopToolResult Screenshot(DesktopScreenshotArgs args)
    {
        if (!OperatingSystem.IsWindows()) return UnsupportedPlatform();
        if (args.Mode == IsolatedMode)
        {
            if (args.WindowId is null)
                return Fail("desktop_window_required", "isolated-mode capture targets one window on the isolated desktop; window_id is required.", permanent: true);
            var isolated = DesktopWin32.RunOnIsolatedDesktop(() => ScreenshotCore(args.WindowId.Value), out var error);
            if (error is not null) return Fail("desktop_isolated_failed", error, permanent: false);
            return isolated;
        }
        if (args.WindowId is { } windowId) return ScreenshotCore(windowId);
        return ScreenshotScreen();
    }

    private DesktopToolResult ScreenshotCore(long windowId)
    {
        using var dpi = DesktopWin32.PerMonitorDpiScope.Enter();
        try
        {
            var hWnd = (nint)windowId;
            if (!IsAliveWindow(hWnd)) return Fail("desktop_window_not_found", $"No window with window_id {windowId}.", permanent: true);
            if (DesktopWin32.IsMinimized(hWnd)) return Fail("desktop_window_minimized", "A minimized window has no surface to capture; restore it first.", permanent: false);
            var (bgra, width, height) = DesktopWin32.CaptureWindowBits(hWnd);
            return SaveScreenshot(bgra, width, height, "window", ToWindowInfo(hWnd));
        }
        catch (Exception exception)
        {
            Logger.Warn(exception, "desktop_screenshot failed");
            return Fail("desktop_capture_failed", exception.Message, permanent: false);
        }
    }

    private DesktopToolResult ScreenshotScreen()
    {
        using var dpi = DesktopWin32.PerMonitorDpiScope.Enter();
        try
        {
            var (bgra, sw, sh) = DesktopWin32.CaptureScreenBits();
            return SaveScreenshot(bgra, sw, sh, "screen", null);
        }
        catch (Exception exception)
        {
            Logger.Warn(exception, "desktop_screenshot failed");
            return Fail("desktop_capture_failed", exception.Message, permanent: false);
        }
    }

    public DesktopToolResult ReadText(DesktopReadTextArgs args)
    {
        if (!OperatingSystem.IsWindows()) return UnsupportedPlatform();
        if (args.Mode == IsolatedMode)
        {
            var isolated = DesktopWin32.RunOnIsolatedDesktop(() => ReadTextCore(args), out var error);
            if (error is not null) return Fail("desktop_isolated_failed", error, permanent: false);
            return isolated;
        }
        return ReadTextCore(args);
    }

    private DesktopToolResult ReadTextCore(DesktopReadTextArgs args)
    {
        var maxChars = Math.Clamp(args.MaxChars ?? 4096, 1, 16384);
        using var dpi = DesktopWin32.PerMonitorDpiScope.Enter();
        var hWnd = (nint)args.WindowId;
        if (!IsAliveWindow(hWnd)) return Fail("desktop_window_not_found", $"No window with window_id {args.WindowId}.", permanent: true);

        var texts = new List<DesktopTextInfo>();
        var budget = maxChars;
        var rootText = DesktopWin32.ReadWindowText(hWnd, Math.Min(budget, 2048), 1000);
        if (rootText.Length > 0)
        {
            texts.Add(new DesktopTextInfo { WindowId = hWnd, ClassName = DesktopWin32.WindowClass(hWnd), Text = rootText });
            budget -= rootText.Length;
        }
        foreach (var child in DesktopWin32.ChildWindows(hWnd, 32))
        {
            if (budget <= 0) break;
            var text = DesktopWin32.ReadWindowText(child, Math.Min(budget, 2048), 1000);
            if (text.Length == 0) continue;
            texts.Add(new DesktopTextInfo { WindowId = child, ClassName = DesktopWin32.WindowClass(child), Text = text });
            budget -= text.Length;
        }
        return new DesktopToolResult
        {
            Ok = true,
            WindowId = hWnd,
            Texts = texts.ToArray(),
            Chars = texts.Sum(t => t.Text?.Length ?? 0),
        };
    }

    // ── action tools ───────────────────────────────────────────────────────────

    public async Task<DesktopToolResult> ClickAsync(DesktopClickArgs args)
    {
        if (!OperatingSystem.IsWindows()) return UnsupportedPlatform();
        var mode = ResolveMode(args.Mode);
        if (mode is null) return Fail("desktop_mode_invalid", "mode must be 'fast', 'silent' or 'isolated'.", permanent: true);
        if (args.X is null || args.Y is null) return Fail("desktop_input_failed", "desktop_click needs integer x and y.", permanent: true);
        var button = args.Button ?? "left";
        if (button is not ("left" or "right" or "middle"))
            return Fail("desktop_input_failed", "button must be left, right or middle.", permanent: true);
        var count = Math.Clamp(args.Count ?? 1, 1, 3);
        var hWnd = args.WindowId is { } value ? (nint)value : 0;
        if (hWnd != 0 && !IsAliveWindow(hWnd))
            return Fail("desktop_window_not_found", $"No window with window_id {args.WindowId}.", permanent: true);

        if (mode == IsolatedMode)
        {
            if (hWnd == 0)
                return Fail("desktop_window_required", "isolated mode delivers input as window messages: window_id is required.", permanent: true);
            await InputGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var isolated = DesktopWin32.RunOnIsolatedDesktop(() => ClickPosted(hWnd, args.X.Value, args.Y.Value, button, count), out var error);
                if (error is not null) return Fail("desktop_isolated_failed", error, permanent: false);
                return isolated;
            }
            finally
            {
                InputGate.Release();
            }
        }

        await InputGate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var dpi = DesktopWin32.PerMonitorDpiScope.Enter();
            var (screenX, screenY) = ResolveScreenPoint(hWnd, args.X.Value, args.Y.Value);
            if (mode == FastMode)
            {
                if (hWnd != 0)
                {
                    if (DesktopWin32.IsMinimized(hWnd))
                        return Fail("desktop_window_minimized", "A minimized window cannot be clicked; restore it first.", permanent: false);
                    if (!DesktopWin32.FocusWindow(hWnd))
                        return Fail("desktop_input_failed", "The OS refused to bring the target window to the foreground.", permanent: false);
                }
                DesktopWin32.ClickAt(screenX, screenY, button, count);
                return new DesktopToolResult { Ok = true, Applied = true, Mode = mode, WindowId = hWnd != 0 ? hWnd : null };
            }

            // silent: the click must land on the named window (or a descendant)
            if (hWnd == 0)
                return Fail("desktop_window_required", "silent mode needs window_id: the click is delivered as a window message, and a bare screen point has no receiver.", permanent: true);
            if (DesktopWin32.IsMinimized(hWnd))
                return Fail("desktop_window_minimized", "A minimized window cannot receive messages; restore it first.", permanent: false);
            var resolved = DesktopWin32.ResolveClickTarget(hWnd, screenX, screenY);
            if (resolved is null)
                return Fail("desktop_post_failed", $"The point ({screenX},{screenY}) is not on window {args.WindowId}; nothing was sent.", permanent: false);
            var (receiver, client) = resolved.Value;
            for (var i = 0; i < count; i++) DesktopWin32.PostClick(receiver, client, button);
            return new DesktopToolResult { Ok = true, Applied = true, Mode = mode, WindowId = hWnd };
        }
        catch (Exception exception)
        {
            Logger.Warn(exception, "desktop_click failed");
            return Fail("desktop_input_failed", exception.Message, permanent: false);
        }
        finally
        {
            InputGate.Release();
        }
    }

    /// <summary>
    /// Message-direct click on a window that may live on any desktop; the caller must already
    /// be attached to that desktop (silent runs it inline, isolated runs it on the attached
    /// thread). Real input cannot reach the isolated desktop: probe-measured, SendInput from
    /// a non-input desktop is rejected with ERROR_ACCESS_DENIED.
    /// </summary>
    private DesktopToolResult ClickPosted(nint hWnd, int x, int y, string button, int count)
    {
        using var dpi = DesktopWin32.PerMonitorDpiScope.Enter();
        if (DesktopWin32.IsMinimized(hWnd))
            return Fail("desktop_window_minimized", "A minimized window cannot receive messages; restore it first.", permanent: false);
        var (screenX, screenY) = ResolveScreenPoint(hWnd, x, y);
        var resolved = DesktopWin32.ResolveClickTarget(hWnd, screenX, screenY);
        if (resolved is null)
            return Fail("desktop_post_failed", $"The point ({screenX},{screenY}) is not on window {hWnd}; nothing was sent.", permanent: false);
        var (receiver, client) = resolved.Value;
        for (var i = 0; i < count; i++) DesktopWin32.PostClick(receiver, client, button);
        return new DesktopToolResult { Ok = true, Applied = true, Mode = IsolatedMode, WindowId = hWnd };
    }

    public async Task<DesktopToolResult> TypeAsync(DesktopTypeArgs args)
    {
        if (!OperatingSystem.IsWindows()) return UnsupportedPlatform();
        var mode = ResolveMode(args.Mode);
        if (mode is null) return Fail("desktop_mode_invalid", "mode must be 'fast', 'silent' or 'isolated'.", permanent: true);
        if (string.IsNullOrEmpty(args.Text)) return Fail("desktop_input_failed", "desktop_type needs a non-empty text.", permanent: true);
        if (args.Text.Length > 4096) return Fail("desktop_input_failed", "text is limited to 4096 characters.", permanent: true);
        var hWnd = args.WindowId is { } value ? (nint)value : 0;
        if (hWnd != 0 && !IsAliveWindow(hWnd))
            return Fail("desktop_window_not_found", $"No window with window_id {args.WindowId}.", permanent: true);

        if (mode == IsolatedMode)
        {
            if (hWnd == 0)
                return Fail("desktop_window_required", "isolated mode delivers text as WM_CHAR messages: window_id is required.", permanent: true);
            await InputGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var isolated = DesktopWin32.RunOnIsolatedDesktop(() => TypePosted(hWnd, args.Text!, args.X, args.Y), out var error);
                if (error is not null) return Fail("desktop_isolated_failed", error, permanent: false);
                return isolated;
            }
            finally
            {
                InputGate.Release();
            }
        }

        await InputGate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var dpi = DesktopWin32.PerMonitorDpiScope.Enter();
            if (mode == FastMode)
            {
                if (hWnd == 0)
                    return Fail("desktop_window_required", "fast typing needs window_id so the target can be focused first.", permanent: true);
                if (DesktopWin32.IsMinimized(hWnd))
                    return Fail("desktop_window_minimized", "A minimized window cannot be typed into; restore it first.", permanent: false);
                if (!DesktopWin32.FocusWindow(hWnd))
                    return Fail("desktop_input_failed", "The OS refused to bring the target window to the foreground.", permanent: false);
                DesktopWin32.TypeUnicode(args.Text);
                return new DesktopToolResult { Ok = true, Applied = true, Mode = mode, Chars = args.Text.Length, WindowId = hWnd };
            }

            if (hWnd == 0)
                return Fail("desktop_window_required", "silent mode needs window_id: the text is delivered as WM_CHAR messages to a specific window.", permanent: true);
            if (DesktopWin32.IsMinimized(hWnd))
                return Fail("desktop_window_minimized", "A minimized window cannot receive messages; restore it first.", permanent: false);
            var receiver = ResolveTextTarget(hWnd, args.X, args.Y);
            DesktopWin32.PostChars(receiver, args.Text);
            return new DesktopToolResult { Ok = true, Applied = true, Mode = mode, Chars = args.Text.Length, WindowId = receiver };
        }
        catch (Exception exception)
        {
            Logger.Warn(exception, "desktop_type failed");
            return Fail("desktop_input_failed", exception.Message, permanent: false);
        }
        finally
        {
            InputGate.Release();
        }
    }

    private DesktopToolResult TypePosted(nint hWnd, string text, int? x, int? y)
    {
        using var dpi = DesktopWin32.PerMonitorDpiScope.Enter();
        if (DesktopWin32.IsMinimized(hWnd))
            return Fail("desktop_window_minimized", "A minimized window cannot receive messages; restore it first.", permanent: false);
        var receiver = ResolveTextTarget(hWnd, x, y);
        DesktopWin32.PostChars(receiver, text);
        return new DesktopToolResult { Ok = true, Applied = true, Mode = IsolatedMode, Chars = text.Length, WindowId = receiver };
    }

    public async Task<DesktopToolResult> PressKeyAsync(DesktopPressKeyArgs args)
    {
        if (!OperatingSystem.IsWindows()) return UnsupportedPlatform();
        var mode = ResolveMode(args.Mode);
        if (mode is null) return Fail("desktop_mode_invalid", "mode must be 'fast', 'silent' or 'isolated'.", permanent: true);
        if (string.IsNullOrWhiteSpace(args.Keys)) return Fail("desktop_input_failed", "desktop_press_key needs a keys value such as 'Enter' or 'ctrl+a'.", permanent: true);
        var hWnd = args.WindowId is { } value ? (nint)value : 0;
        if (hWnd != 0 && !IsAliveWindow(hWnd))
            return Fail("desktop_window_not_found", $"No window with window_id {args.WindowId}.", permanent: true);

        var keys = ParseKeyChord(args.Keys);
        if (keys is null)
            return Fail("desktop_input_failed", $"Unsupported key spec '{args.Keys}'.", permanent: true);

        if (mode == IsolatedMode)
        {
            if (hWnd == 0)
                return Fail("desktop_window_required", "isolated mode delivers keys as window messages: window_id is required.", permanent: true);
            if (keys.Length > 1)
                return Fail("desktop_silent_modifier_unsupported",
                    "isolated mode cannot send key chords: posted messages do not maintain keyboard state.",
                    permanent: true);
            await InputGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var isolated = DesktopWin32.RunOnIsolatedDesktop(() =>
                {
                    using var dpi = DesktopWin32.PerMonitorDpiScope.Enter();
                    var receiver = ResolveTextTarget(hWnd, null, null);
                    DesktopWin32.PostKey(receiver, keys[0]);
                    return new DesktopToolResult { Ok = true, Applied = true, Mode = IsolatedMode, WindowId = receiver };
                }, out var error);
                if (error is not null) return Fail("desktop_isolated_failed", error, permanent: false);
                return isolated;
            }
            finally
            {
                InputGate.Release();
            }
        }

        await InputGate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var dpi = DesktopWin32.PerMonitorDpiScope.Enter();
            if (mode == FastMode)
            {
                if (hWnd != 0)
                {
                    if (DesktopWin32.IsMinimized(hWnd))
                        return Fail("desktop_window_minimized", "A minimized window cannot receive keys; restore it first.", permanent: false);
                    if (!DesktopWin32.FocusWindow(hWnd))
                        return Fail("desktop_input_failed", "The OS refused to bring the target window to the foreground.", permanent: false);
                }
                DesktopWin32.PressKeys(keys);
                return new DesktopToolResult { Ok = true, Applied = true, Mode = mode, WindowId = hWnd != 0 ? hWnd : null };
            }

            if (hWnd == 0)
                return Fail("desktop_window_required", "silent mode needs window_id: keys are delivered as window messages.", permanent: true);
            if (keys.Length > 1)
                return Fail("desktop_silent_modifier_unsupported",
                    "silent mode cannot send key chords: posted messages do not maintain keyboard state. Use fast mode, or drive the control semantically.",
                    permanent: true);
            var receiver = ResolveTextTarget(hWnd, null, null);
            DesktopWin32.PostKey(receiver, keys[0]);
            return new DesktopToolResult { Ok = true, Applied = true, Mode = mode, WindowId = receiver };
        }
        catch (Exception exception)
        {
            Logger.Warn(exception, "desktop_press_key failed");
            return Fail("desktop_input_failed", exception.Message, permanent: false);
        }
        finally
        {
            InputGate.Release();
        }
    }

    /// <summary>
    /// Launches a process onto the isolated desktop. This is how windows get there: a null
    /// lpDesktop inherits the parent process's desktop, not the calling thread's, so apps
    /// cannot "move" onto the isolated desktop after the fact (probe-measured, E8).
    /// </summary>
    public DesktopToolResult LaunchApp(DesktopLaunchArgs args)
    {
        if (!OperatingSystem.IsWindows()) return UnsupportedPlatform();
        if (string.IsNullOrWhiteSpace(args.Command))
            return Fail("desktop_input_failed", "desktop_launch_app needs a command (an executable path).", permanent: true);
        if (args.Command.Length > 1024 || (args.Arguments?.Length ?? 0) > 4096)
            return Fail("desktop_input_failed", "command/arguments are too long.", permanent: true);
        var pid = DesktopWin32.LaunchOnIsolatedDesktop(args.Command, args.Arguments, args.WorkingDirectory, out var error);
        if (pid == 0)
            return Fail("desktop_isolated_failed", error ?? "CreateProcessW failed.", permanent: false);
        return new DesktopToolResult { Ok = true, Applied = true, Mode = IsolatedMode, Pid = (int)pid };
    }

    // ── mode resolution ────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the effective mode for one call. The per-call argument wins; behind it the
    /// frozen execution-context setting, then the environment, then the config file, then
    /// fast. Returns null when the requested value is not fast/silent; a broken config file
    /// is surfaced as desktop_config_invalid through the same channel.
    /// </summary>
    internal string? ResolveMode(string? requested)
    {
        if (requested is not null)
        {
            if (requested is not (FastMode or SilentMode or IsolatedMode)) return null;
            return requested;
        }

        var context = ToolExecutionContext.Current?.Setting("desktop", "mode");
        if (context is { ValueKind: JsonValueKind.String } value)
        {
            var setting = value.GetString();
            if (setting is not (FastMode or SilentMode or IsolatedMode))
                throw new DesktopConfigurationException("desktop_config_invalid: execution-context setting desktop.mode must be 'fast' or 'silent'.");
            return setting;
        }

        var environment = Environment.GetEnvironmentVariable("TINADEC_COMPUTER_USE_MODE");
        if (!string.IsNullOrEmpty(environment))
        {
            if (environment is not (FastMode or SilentMode or IsolatedMode))
                throw new DesktopConfigurationException("desktop_config_invalid: TINADEC_COMPUTER_USE_MODE must be 'fast' or 'silent'.");
            return environment;
        }

        var configured = ReadConfiguredDefaultMode();
        return configured ?? FastMode;
    }

    /// <summary>
    /// Reads default_mode from TINADEC_HOME/config/computer-use.json. Any malformed file is
    /// an error rather than a silent fallback: a corrupt 'silent' must not quietly become
    /// the more intrusive 'fast'.
    /// </summary>
    private string? ReadConfiguredDefaultMode()
    {
        var home = _homeProvider();
        if (string.IsNullOrWhiteSpace(home)) return null;
        var file = Path.Combine(home, "config", "computer-use.json");
        if (!File.Exists(file)) return null;
        string value;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("default_mode", out var mode)
                || mode.ValueKind != JsonValueKind.String)
                throw new DesktopConfigurationException($"desktop_config_invalid: {file} must be an object with a string default_mode.");
            value = mode.GetString()!;
        }
        catch (JsonException exception)
        {
            throw new DesktopConfigurationException($"desktop_config_invalid: {file} is not valid JSON ({exception.Message}).");
        }
        if (value is not (FastMode or SilentMode or IsolatedMode))
            throw new DesktopConfigurationException($"desktop_config_invalid: default_mode in {file} must be 'fast' or 'silent', got '{value}'.");
        return value;
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static bool IsAliveWindow(nint hWnd) => DesktopWin32.WindowClass(hWnd).Length > 0 || DesktopWin32.WindowBounds(hWnd).Width != 0;

    private static DesktopToolResult UnsupportedPlatform() =>
        Fail("desktop_unsupported_platform", "Desktop tools only exist on Windows.", permanent: true);

    private static DesktopToolResult Fail(string code, string message, bool permanent) => new()
    {
        Ok = false,
        Code = code,
        Message = message,
        Permanent = permanent,
    };

    /// <summary>
    /// Converts the tool's coordinate pair to screen coordinates. With a window_id the pair
    /// is client-relative of that window; without one it is already virtual-screen absolute
    /// (and may be negative for monitors placed left of the primary).
    /// </summary>
    private static (int ScreenX, int ScreenY) ResolveScreenPoint(nint hWnd, int x, int y)
    {
        if (hWnd == 0) return (x, y);
        var point = DesktopWin32.ClientToScreen(hWnd, x, y);
        return (point.X, point.Y);
    }

    /// <summary>
    /// Picks the receiver for silent typing: the control at the given client point when one
    /// is supplied, else the first enabled edit-class descendant, else the window itself.
    /// </summary>
    private static nint ResolveTextTarget(nint hWnd, int? x, int? y)
    {
        if (x is { } px && y is { } py)
        {
            var screen = DesktopWin32.ClientToScreen(hWnd, px, py);
            var resolved = DesktopWin32.ResolveClickTarget(hWnd, screen.X, screen.Y);
            if (resolved is { } hit) return hit.Receiver;
        }
        foreach (var child in DesktopWin32.ChildWindows(hWnd, 32))
        {
            var className = DesktopWin32.WindowClass(child);
            if (className.Contains("Edit", StringComparison.OrdinalIgnoreCase) && DesktopWin32.IsEnabled(child))
                return child;
        }
        return hWnd;
    }

    private static DesktopWindowInfo ToWindowInfo(DesktopWin32.WindowRow row) => new()
    {
        WindowId = row.Handle,
        Pid = (int)row.ProcessId,
        ProcessName = string.IsNullOrEmpty(row.ProcessName) ? null : row.ProcessName,
        Title = string.IsNullOrEmpty(row.Title) ? null : row.Title,
        ClassName = row.ClassName,
        Bounds = new DesktopBounds { X = row.X, Y = row.Y, Width = row.Width, Height = row.Height },
        IsForeground = row.Foreground,
        IsMinimized = row.Minimized,
    };

    private static DesktopWindowInfo ToWindowInfo(nint hWnd)
    {
        var (x, y, width, height) = DesktopWin32.WindowBounds(hWnd);
        DesktopWin32.ThreadIdOf(hWnd, out var pid);
        var title = DesktopWin32.WindowTitle(hWnd);
        return new DesktopWindowInfo
        {
            WindowId = hWnd,
            Pid = (int)pid,
            ProcessName = null,
            Title = string.IsNullOrEmpty(title) ? null : title,
            ClassName = DesktopWin32.WindowClass(hWnd),
            Bounds = new DesktopBounds { X = x, Y = y, Width = width, Height = height },
            IsForeground = DesktopWin32.GetForeground() == hWnd,
            IsMinimized = DesktopWin32.IsMinimized(hWnd),
        };
    }

    /// <summary>
    /// Encodes and saves one capture: the full-resolution PNG at <c>path</c>, plus — when the
    /// long edge exceeds <see cref="MaxContextImageEdge"/> — a downscaled <c>*-ctx.png</c> copy
    /// referenced by the <c>image</c> object. Core loads <c>image.path</c> into the model
    /// conversation; the full original never leaves disk.
    /// </summary>
    private DesktopToolResult SaveScreenshot(byte[] bgra, int width, int height, string target, DesktopWindowInfo? window)
    {
        var home = _homeProvider();
        var directory = string.IsNullOrWhiteSpace(home)
            ? Path.Combine(Path.GetTempPath(), "tinadec-computer-use")
            : Path.Combine(home, "state", "computer-use", "screenshots");
        Directory.CreateDirectory(directory);
        var counter = Interlocked.Increment(ref _screenshotCounter);
        var path = Path.Combine(directory, $"desktop-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{counter}.png");
        var png = DesktopWin32.PngEncoder.Encode(bgra, width, height);
        File.WriteAllBytes(path, png);

        var (context, contextWidth, contextHeight) = DesktopWin32.Downscale(bgra, width, height, MaxContextImageEdge);
        string contextPath;
        if (ReferenceEquals(context, bgra))
        {
            // small enough: the full capture is its own context copy, no duplicate file
            contextPath = path;
        }
        else
        {
            contextPath = Path.Combine(directory, $"desktop-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{counter}-ctx.png");
            File.WriteAllBytes(contextPath, DesktopWin32.PngEncoder.Encode(context, contextWidth, contextHeight));
        }

        return new DesktopToolResult
        {
            Ok = true,
            Path = path,
            Bytes = png.Length,
            Width = width,
            Height = height,
            Target = target,
            Window = window,
            Image = new DesktopImageInfo
            {
                Path = contextPath,
                MediaType = "image/png",
                Width = contextWidth,
                Height = contextHeight,
                Bytes = new FileInfo(contextPath).Length,
            },
        };
    }

    /// <summary>
    /// Parses a key spec such as "Enter", "a" or "ctrl+shift+s" into virtual keys ordered
    /// modifiers-first. Supports the names desktop automation needs; returns null for
    /// anything else rather than guessing a wrong key.
    /// </summary>
    internal static ushort[]? ParseKeyChord(string spec)
    {
        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim().ToLowerInvariant())
            .ToArray();
        if (parts.Length == 0) return null;
        var keys = new List<ushort>();
        foreach (var part in parts)
        {
            var key = part switch
            {
                "ctrl" or "control" => (ushort)0x11,
                "shift" => (ushort)0x10,
                "alt" => (ushort)0x12,
                "win" or "meta" or "cmd" => (ushort)0x5B,
                "enter" or "return" => (ushort)0x0D,
                "tab" => (ushort)0x09,
                "esc" or "escape" => (ushort)0x1B,
                "backspace" => (ushort)0x08,
                "delete" or "del" => (ushort)0x2E,
                "space" => (ushort)0x20,
                "up" => (ushort)0x26,
                "down" => (ushort)0x28,
                "left" => (ushort)0x25,
                "right" => (ushort)0x27,
                "home" => (ushort)0x24,
                "end" => (ushort)0x23,
                "pageup" => (ushort)0x21,
                "pagedown" => (ushort)0x22,
                _ => ParseSimpleKey(part),
            };
            if (key is not { } value) return null;
            keys.Add(value);
        }
        return keys.ToArray();
    }

    private static ushort? ParseSimpleKey(string part) => part switch
    {
        _ when part.Length == 1 && part[0] is >= 'a' and <= 'z' => (ushort)(part[0] - 'a' + 0x41),
        _ when part.Length == 1 && part[0] is >= '0' and <= '9' => (ushort)(part[0] - '0' + 0x30),
        _ when part.Length == 2 && part[0] == 'f' && part[1] is >= '1' and <= '9' => (ushort)(0x70 + part[1] - '1'),
        "," => (ushort)0xBC,
        "." => (ushort)0xBE,
        "/" => (ushort)0xBF,
        ";" => (ushort)0xBA,
        "'" => (ushort)0xDE,
        "[" => (ushort)0xDB,
        "]" => (ushort)0xDD,
        "\\" => (ushort)0xDC,
        "-" => (ushort)0xBD,
        "=" => (ushort)0xBB,
        "`" => (ushort)0xC0,
        _ => null,
    };
}
