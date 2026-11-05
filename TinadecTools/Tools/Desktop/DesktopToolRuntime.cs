using System.Runtime.InteropServices;
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
///
/// On top of that, desktop_click carries an opt-in coordinate-compensation layer for vision
/// models that report clicks in a rescaled version of the image they were shown. It is off
/// unless the host enables it (execution-context setting or computer-use.json
/// `coordinate_compensation`), it maps coordinates only through MEASURED per-model rows in
/// TINADEC_HOME/config/coordinate-profiles.json (missing rows refuse: fail-closed), and every
/// compensated click is followed by a semantic verification that reads the window's text
/// before and after, reporting verified / failed / unverified with before/after screenshots.
/// Three consecutive failed verifications in one run trip a circuit breaker that suspends
/// coordinate clicking for the rest of that run (desktop_coordinate_compensation_suspended)
/// instead of letting the model keep clicking blind.
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

    // ── served-capture tracking (input for coordinate compensation) ────────────
    // The compensation layer needs the shape of the image the model actually saw — the
    // downscaled ctx copy, not the full capture. The runtime itself served that image, so
    // it remembers the most recent one per run; a click without a served capture refuses
    // instead of compensating against a shape it cannot know. Key "" covers callers with
    // no execution context (tests, harnesses).

    /// <summary>The most recent capture handed to the model for one run.</summary>
    internal sealed record ServedCapture(long? WindowId, int FullWidth, int FullHeight, int ContextWidth, int ContextHeight, string ContextPath);

    private readonly object _captureLock = new();
    private readonly Dictionary<string, ServedCapture> _lastCapture = new();
    private readonly Queue<string> _captureOrder = new();
    private const int RememberedCaptures = 64;

    /// <summary>The most recent desktop_find_element page for one run: the index /
    /// automation_id namespace desktop_click's element path resolves against. Each entry
    /// keeps its live UIA element so the click paths (native PostClick / Invoke) act on
    /// exactly what was found.</summary>
    private readonly Dictionary<string, (long WindowId, List<(DesktopAutomation.ElementInfo Info, DesktopAutomation.IUIAutomationElement Raw)> Page)> _lastElements = new();

    internal ServedCapture? LastServedCapture(string? runId = null)
    {
        lock (_captureLock) return _lastCapture.TryGetValue(runId ?? ToolExecutionContext.Current?.RunId ?? "", out var capture) ? capture : null;
    }

    private void RememberCapture(ServedCapture capture)
    {
        var key = ToolExecutionContext.Current?.RunId ?? "";
        lock (_captureLock)
        {
            if (_lastCapture.TryAdd(key, capture)) _captureOrder.Enqueue(key);
            else _lastCapture[key] = capture;
            while (_captureOrder.Count > RememberedCaptures)
                _lastCapture.Remove(_captureOrder.Dequeue());
        }
    }

    // ── compensation circuit breaker ───────────────────────────────────────────
    // Semantic verification judges one click; the breaker judges the run. A model that
    // keeps clicking dead space (wrong scale, drifted answers, misread scene) must be
    // stopped after a few consecutive failures instead of hammering the desktop while the
    // model "retries" with the same broken coordinates. Counted per run; a verified click
    // resets the streak; `unverified` (no readable text) is not evidence of a bad scale and
    // does not count. The breaker lives only in memory: a new run starts clean.

    private const int CompensationFailureLimit = 3;
    private readonly Dictionary<string, int> _consecutiveCompensationFailures = new();
    private readonly HashSet<string> _compensationSuspended = new();

    internal const string CompensationSuspendedCode = "desktop_coordinate_compensation_suspended";

    private bool IsCompensationSuspended()
    {
        lock (_captureLock) return _compensationSuspended.Contains(ToolExecutionContext.Current?.RunId ?? "");
    }

    /// <summary>Records one failed verification; returns true when THIS failure trips the breaker.</summary>
    private bool NoteCompensationFailure()
    {
        var key = ToolExecutionContext.Current?.RunId ?? "";
        lock (_captureLock)
        {
            var count = _consecutiveCompensationFailures.GetValueOrDefault(key) + 1;
            _consecutiveCompensationFailures[key] = count;
            if (count >= CompensationFailureLimit)
            {
                _compensationSuspended.Add(key);
                return true;
            }
            return false;
        }
    }

    private void ResetCompensationFailures()
    {
        lock (_captureLock) _consecutiveCompensationFailures.Remove(ToolExecutionContext.Current?.RunId ?? "");
    }

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
        var button = args.Button ?? "left";
        if (button is not ("left" or "right" or "middle"))
            return Fail("desktop_input_failed", "button must be left, right or middle.", permanent: true);
        var count = Math.Clamp(args.Count ?? 1, 1, 3);
        var hWnd = args.WindowId is { } value ? (nint)value : 0;
        if (hWnd != 0 && !IsAliveWindow(hWnd))
            return Fail("desktop_window_not_found", $"No window with window_id {args.WindowId}.", permanent: true);
        if (args.X is null || args.Y is null)
        {
            if (args.ElementIndex is null && args.ElementAutomationId is null)
                return Fail("desktop_input_failed", "desktop_click needs x and y, or element_index / element_automation_id from desktop_find_element.", permanent: true);
        }
        else if (args.ElementIndex is not null || args.ElementAutomationId is not null)
        {
            return Fail("desktop_input_failed", "pass either x/y or an element reference, not both.", permanent: true);
        }

        // ── element click: UIA semantics, no model coordinates anywhere ────────────
        // The precise path for small targets. When the element has its own Win32 window
        // (classic controls), the click is posted straight to THAT window at its client
        // center - no screen-coordinate translation exists in that chain, so no DPI space
        // can mix (round 9 measured how screen-space translation shifts small targets).
        // Virtual elements (WPF/UWP, no native handle) fall back to their UIA rectangle
        // through the client-coordinate delivery chain.
        if (args.ElementIndex is not null || args.ElementAutomationId is not null)
        {
            var (elementError, element, raw, elementIndex, elementWindow) = ResolveClickElement(args);
            if (elementError is not null) return elementError;
            if (DesktopWin32.IsHigherIntegrityWindow((nint)elementWindow))
                return Fail(DesktopTargetElevatedCode,
                    "the target window belongs to a higher-integrity (elevated) process; the OS refuses input to it from this process. Ask the user to operate that window, or run the agent's host elevated.",
                    permanent: true);
            hWnd = (nint)elementWindow;
            var before = ReadTextsForVerification(hWnd, mode);
            DesktopToolResult elementResult;
            string delivery;
            if (element!.NativeWindowHandle != 0)
            {
                // exact path: post to the element's own window; every coordinate is
                // window-relative, so no DPI space can shift it
                delivery = "native-post";
                await InputGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (mode == IsolatedMode)
                    {
                        var isolated = DesktopWin32.RunOnIsolatedDesktop(() => PostClickToNativeElement(element.NativeWindowHandle, button, count), out var runError);
                        elementResult = runError is not null ? Fail("desktop_isolated_failed", runError, permanent: false) : isolated;
                    }
                    else
                    {
                        elementResult = PostClickToNativeElement(element.NativeWindowHandle, button, count);
                    }
                }
                finally
                {
                    InputGate.Release();
                }
            }
            else
            {
                // virtual element (WPF/UWP/WinUI): pattern-aware semantic click in
                // stability order (Invoke > Toggle > SelectionItem > ExpandCollapse),
                // coordinate-free; the rect-ratio path is the last resort
                string? via = null;
                try
                {
                    via = DesktopAutomation.ClickByPattern(raw!);
                }
                catch (COMException exception)
                {
                    return Fail("desktop_element_invoke_failed",
                        $"the element supports a click pattern but invoking it failed ({exception.Message}). Read the element's state with desktop_find_element before retrying.",
                        permanent: false);
                }
                if (via is not null)
                {
                    delivery = via;
                    elementResult = new DesktopToolResult { Ok = true, Applied = true, Mode = mode, WindowId = hWnd };
                }
                else
                {
                    if (element.Right <= element.Left || element.Bottom <= element.Top)
                        return Fail("desktop_element_not_clickable",
                            "the element supports no click pattern (Invoke/Toggle/SelectionItem/ExpandCollapse) and reports an empty rectangle; it cannot be clicked. Use desktop_type / desktop_press_key or another control.",
                            permanent: true);
                    delivery = "rect-center";
                    var client = UiaRectToPhysicalClient(hWnd, element);
                    elementResult = await DeliverClickAsync(
                        new DesktopClickArgs { X = client.X, Y = client.Y, WindowId = elementWindow, Button = args.Button, Count = args.Count, Mode = args.Mode },
                        mode, hWnd, button, count).ConfigureAwait(false);
                }
            }
            elementResult.Element = ToElementInfo(element, elementIndex);
            elementResult.Delivery = delivery;
            if (elementResult.Ok)
            {
                elementResult.Verification = VerifyAfterClick(hWnd, mode, before, new ServedCapture(hWnd, 0, 0, 0, 0, ""));
                elementResult.Message = elementResult.Verification.Message;
            }
            return elementResult;
        }

        if (args.X is null || args.Y is null)
            return Fail("desktop_input_failed", "desktop_click needs x and y, or element_index / element_automation_id from desktop_find_element.", permanent: true);

        if (hWnd != 0 && DesktopWin32.IsHigherIntegrityWindow(hWnd))
            return Fail(DesktopTargetElevatedCode,
                "the target window belongs to a higher-integrity (elevated) process; the OS refuses input to it from this process. Ask the user to operate that window, or run the agent's host elevated.",
                permanent: true);

        // ── coordinate compensation (host-opt-in, fail-closed) ────────────────────
        // Some vision models report clicks in a rescaled version of the image they were
        // shown (deepseek-v4.1-flash answers ≈ shown-image × 0.57, measured). When the
        // host enables this layer, the reported coordinates are mapped onto the window
        // through a MEASURED per-model profile; anything unmeasured refuses the click,
        // because a guessed scale moves clicks by a quarter screen without any error.
        var compensation = ResolveCoordinateCompensation();
        DesktopCompensationInfo? compensationInfo = null;
        ServedCapture? servedCapture = null;
        DesktopTextInfo[]? beforeTexts = null;
        if (compensation is { } config)
        {
            // the circuit breaker gates the whole layer for this run: after consecutive
            // failed verifications, more coordinate clicks are noise, not progress
            if (IsCompensationSuspended())
                return Fail(CompensationSuspendedCode,
                    $"coordinate compensation is suspended for this run: {CompensationFailureLimit} consecutive compensated clicks failed text verification. This model/image-shape combination is not reliable for coordinate clicking — stop retrying by coordinates; re-verify with desktop_read_text and semantic tools, or ask the user. A new run starts with compensation re-enabled.",
                    permanent: true);
            var (error, plan) = PlanCoordinateCompensation(config, args);
            if (error is not null) return error;
            args = plan!.Args;
            compensationInfo = plan.Info;
            servedCapture = plan.Capture;
        }

        // ── occlusion guard: refuse to click through whatever covers the target ───
        // Measured on this machine: an orphaned Shell_SystemDim full-screen topmost layer
        // silently swallowed clicks (the click "succeeded" and hit nothing). A silent
        // miss is bad; a silent hit on the WRONG window's OK/Allow button is an accident.
        // The hit-test must run in the same coordinate space as the delivery itself, so
        // for isolated windows both ClientToScreen and WindowFromPoint run on the
        // isolated desktop's thread, exactly like the delivery chain does.
        if (hWnd != 0)
        {
            if (mode == IsolatedMode)
            {
                var isolated = DesktopWin32.RunOnIsolatedDesktop(() =>
                {
                    var screen = DesktopWin32.ClientToScreen(hWnd, args.X!.Value, args.Y!.Value);
                    return GuardPoint(hWnd, screen.X, screen.Y);
                }, out var runError);
                if (runError is not null) return Fail("desktop_isolated_failed", runError, permanent: false);
                if (isolated is not null) return isolated;
            }
            else
            {
                using var dpi = DesktopWin32.PerMonitorDpiScope.Enter();
                var screen = DesktopWin32.ClientToScreen(hWnd, args.X!.Value, args.Y!.Value);
                if (GuardPoint(hWnd, screen.X, screen.Y) is { } guard) return guard;
            }
        }

        // pre-click text baseline for the semantic verdict after the click
        if (compensationInfo is not null && hWnd != 0)
            beforeTexts = ReadTextsForVerification(hWnd, mode);

        var result = await DeliverClickAsync(args, mode, hWnd, button, count).ConfigureAwait(false);
        if (compensationInfo is null) return result;

        result.Compensation = compensationInfo;
        if (!result.Ok) return result;
        result.Verification = VerifyAfterClick(hWnd, mode, beforeTexts, servedCapture!);
        result.Message = result.Verification.Message;
        if (result.Verification.Status == VerificationFailed)
        {
            if (NoteCompensationFailure())
                result.Message += $" Coordinate compensation is now SUSPENDED for this run ({CompensationFailureLimit} consecutive unverified clicks): do not keep clicking by coordinates.";
        }
        else if (result.Verification.Status == VerificationVerified)
        {
            ResetCompensationFailures();
        }
        return result;
    }

    /// <summary>
    /// Posts the click to the element's own Win32 window at its client-area center. Every
    /// coordinate in this chain is window-relative, so DPI virtualization cannot shift it
    /// (round 9 measured screen-space translation moving small-target clicks; this path
    /// has no screen space). Only valid for elements that expose a native window handle.
    /// </summary>
    private static DesktopToolResult PostClickToNativeElement(nint elementHwnd, string button, int count)
    {
        var (width, height) = DesktopWin32.ClientSize(elementHwnd);
        if (width <= 0 || height <= 0)
            return Fail("desktop_element_not_clickable", "the element's client area is empty; it cannot receive a posted click.", permanent: true);
        var center = new DesktopWin32.POINT { X = width / 2, Y = height / 2 };
        for (var i = 0; i < count; i++) DesktopWin32.PostClick(elementHwnd, center, button);
        return new DesktopToolResult { Ok = true, Applied = true, WindowId = elementHwnd };
    }

    /// <summary>Delivers text to the element's own edit window: WM_CHAR code units, or a
    /// clipboard paste when the text carries surrogate pairs into a classic Edit control
    /// (which mangles lone surrogates into '?'). The delivery is reported in the result.</summary>
    private static DesktopToolResult PostCharsToNativeElement(nint elementHwnd, string text)
    {
        var delivery = DesktopWin32.PostText(elementHwnd, text);
        return new DesktopToolResult { Ok = true, Applied = true, WindowId = elementHwnd, Chars = text.Length, Delivery = delivery };
    }

    /// <summary>Refuses the click when the screen point is occupied by anything other than
    /// the target window's tree. Runs in the desktop the click will land on.</summary>
    private static DesktopToolResult? GuardPoint(nint hWnd, int screenX, int screenY)
    {
        var (root, className, title) = DesktopWin32.WindowRootAtPoint(screenX, screenY);
        if (root == hWnd) return null;
        var coveredBy = root == 0
            ? "no window (bare desktop region)"
            : $"window class '{className}' titled '{title}'";
        return Fail("desktop_point_occluded",
            $"the point ({screenX},{screenY}) is occupied by {coveredBy}, not by the target window; refusing to click through it. Bring the target window to the front (or dismiss the overlay) and retry.",
            permanent: false);
    }

    // ── element finding and element clicking ───────────────────────────────────

    /// <summary>
    /// Enumerates the target window's UI Automation tree, filtered by the given fragments,
    /// and caches the returned page for this run so desktop_click can reference elements by
    /// index or automation id. Works across desktops of this session, TinadecIsolated
    /// included (probe-measured) - UIA reaches windows the input desktop cannot. Large
    /// trees: page with offset, hide offscreen elements with offscreen=false, and prefer
    /// control_type filters - a tree of hundreds needs one filtered call, not ten pages.
    /// </summary>
    public DesktopToolResult FindElement(DesktopFindElementArgs args)
    {
        if (!OperatingSystem.IsWindows()) return UnsupportedPlatform();
        var mode = ResolveMode(args.Mode);
        if (mode is null) return Fail("desktop_mode_invalid", "mode must be 'fast', 'silent' or 'isolated'.", permanent: true);
        var hWnd = (nint)args.WindowId;
        if (!IsAliveWindow(hWnd)) return Fail("desktop_window_not_found", $"No window with window_id {args.WindowId}.", permanent: true);
        if (DesktopWin32.IsHigherIntegrityWindow(hWnd))
            return Fail(DesktopTargetElevatedCode,
                "the target window belongs to a process running at a HIGHER integrity level (elevated/admin). UI Automation returns no elements and input is refused by the OS for such windows - this is a policy boundary, not a missing control tree. Ask the user to operate that window, or run the agent's host elevated.",
                permanent: true);
        var limit = Math.Clamp(args.Limit ?? 20, 1, 50);
        var offset = Math.Max(0, args.Offset ?? 0);
        var includeOffscreen = args.Offscreen ?? true;
        var scroll = args.Scroll ?? false;

        var (error, page, total, scrollSteps) = DesktopAutomation.FindElements(hWnd, args.Name, args.AutomationId, args.ControlType, args.ClassName, limit, offset, includeOffscreen, scroll);
        if (error is not null) return Fail("desktop_uia_failed", error, permanent: false);

        // scroll=true and still nothing: the whole virtualized list was paged through -
        // a DEFINITE not-found, not the ambiguous empty page (probe round 12: '视频' could
        // not be told apart from "not realized yet"; now the scroll says which it was)
        if (scroll && total == 0 && scrollSteps > 0)
            return Fail("desktop_element_not_found",
                $"no element matches the filter after scrolling the entire virtualized list ({scrollSteps} page steps to the bottom). The target is genuinely absent from this window, not merely offscreen.",
                permanent: true);

        lock (_captureLock)
        {
            _lastElements[ToolExecutionContext.Current?.RunId ?? ""] = (args.WindowId, page.ToList());
        }
        Logger.Info("desktop_find_element: window {WindowId} filter name='{Name}' type='{Type}' offset={Offset} scroll={Scroll} -> {Total} matches, returning {Count}", args.WindowId, args.Name, args.ControlType, offset, scroll, total, page.Length);

        var result = new DesktopToolResult
        {
            Ok = true,
            WindowId = hWnd,
            Count = page.Length,
            Total = total,
            Offset = offset,
            Elements = page.Select((entry, index) => ToElementInfo(entry.Info, index)).ToArray(),
        };
        var shownTo = offset + page.Length;
        if (total > shownTo)
            result.Message = $"{total} elements match the filter; showing {offset}..{shownTo - 1}. Continue with offset={shownTo}, or narrow the filter (name / control_type / offscreen=false) to find things in one call.";
        return result;
    }

    internal const string DesktopTargetElevatedCode = "desktop_target_elevated";

    /// <summary>
    /// Sets a range-capable element's value (slider, progress, zoom) by reference through
    /// the RangeValue pattern, and reads the value back as the evidence. Text fields are
    /// not set here - desktop_type's element path owns those (Value pattern).
    /// </summary>
    public DesktopToolResult SetValue(DesktopSetValueArgs args)
    {
        if (!OperatingSystem.IsWindows()) return UnsupportedPlatform();
        var mode = ResolveMode(args.Mode);
        if (mode is null) return Fail("desktop_mode_invalid", "mode must be 'fast', 'silent' or 'isolated'.", permanent: true);
        var (error, element, raw, elementIndex, elementWindow) = ResolveClickElement(new DesktopClickArgs { ElementIndex = args.ElementIndex, ElementAutomationId = args.ElementAutomationId, WindowId = args.WindowId });
        if (error is not null) return error;

        var (set, readBack, setError) = DesktopAutomation.TrySetRangeValue(raw!, args.Value);
        if (!set)
            return Fail("desktop_element_not_range_capable",
                $"{setError}. desktop_set_value is for slider/progress/zoom controls; text fields take desktop_type's element path.",
                permanent: true);

        Logger.Info("desktop_set_value: element '{Name}' set to {Value}, read back {ReadBack}", element!.Name, args.Value, readBack);
        return new DesktopToolResult
        {
            Ok = true,
            Applied = true,
            WindowId = elementWindow,
            Element = ToElementInfo(element, elementIndex),
            Value = readBack,
            Message = readBack is { } value
                ? $"value set; the element reports {value} (requested {args.Value})."
                : $"value set; the element reports no readable value (requested {args.Value}).",
        };
    }

    private static DesktopElementInfo ToElementInfo(DesktopAutomation.ElementInfo element, int index) => new()
    {
        Index = index,
        Name = element.Name,
        AutomationId = element.AutomationId,
        ControlType = DesktopAutomation.ControlTypeName(element.ControlType),
        ClassName = element.ClassName,
        Bounds = new DesktopBounds { X = element.Left, Y = element.Top, Width = element.Width, Height = element.Height },
        IsEnabled = element.IsEnabled,
        IsOffscreen = element.IsOffscreen,
    };

    private (DesktopToolResult? Error, DesktopAutomation.ElementInfo? Element, DesktopAutomation.IUIAutomationElement? Raw, int Index, long WindowId) ResolveClickElement(DesktopClickArgs args)
    {
        (long WindowId, List<(DesktopAutomation.ElementInfo Info, DesktopAutomation.IUIAutomationElement Raw)> Page) page = (0, []);
        lock (_captureLock) _lastElements.TryGetValue(ToolExecutionContext.Current?.RunId ?? "", out page);
        if (page.Page is null || page.Page.Count == 0)
            return (Fail("desktop_element_context_missing",
                "desktop_click by element needs a desktop_find_element in this run first: its result is the index/automation_id namespace. Run desktop_find_element on the target window, then click the returned element.",
                permanent: true), null, null, 0, 0);
        var cached = page;

        if (args.WindowId is { } requested && requested != cached.WindowId)
            return (Fail("desktop_element_context_missing",
                $"the cached element list belongs to window_id {cached.WindowId}, but the click targets window_id {requested}. Run desktop_find_element on the target window first.",
                permanent: true), null, null, 0, 0);

        DesktopAutomation.ElementInfo element;
        DesktopAutomation.IUIAutomationElement raw;
        int elementIndex;
        if (args.ElementIndex is { } index)
        {
            if (index < 0 || index >= cached.Page.Count)
                return (Fail("desktop_element_not_found",
                    $"element_index {index} is outside the cached desktop_find_element result (0..{cached.Page.Count - 1}). Re-run desktop_find_element and use a returned index.",
                    permanent: true), null, null, 0, 0);
            (element, raw) = cached.Page[index];
            elementIndex = index;
        }
        else
        {
            elementIndex = cached.Page.FindIndex(candidate => string.Equals(candidate.Info.AutomationId, args.ElementAutomationId, StringComparison.OrdinalIgnoreCase));
            if (elementIndex < 0)
                return (Fail("desktop_element_not_found",
                    $"no element with automation_id '{args.ElementAutomationId}' in the cached desktop_find_element result. Re-run desktop_find_element with automation_id filter to see what exists.",
                    permanent: true), null, null, 0, 0);
            (element, raw) = cached.Page[elementIndex];
        }

        if (element.IsOffscreen)
        {
            // bring the element into the visible area through its scroll container; if the
            // container cannot scroll (or the window is minimized), fail explicitly instead
            // of clicking a point that cannot be on the element
            var scrolled = false;
            try
            {
                scrolled = DesktopAutomation.TryScrollIntoView(raw);
            }
            catch (COMException)
            {
                scrolled = false;
            }
            if (!scrolled)
                return (Fail("desktop_element_offscreen",
                    "the element is outside the visible area and its container cannot scroll it into view (or the window is minimized). Scroll or restore the window first, then re-run desktop_find_element.",
                    permanent: false), null, null, 0, 0);
        }

        return (null, element, raw, elementIndex, cached.WindowId);
    }

    /// <summary>
    /// Converts a UIA element rectangle (which lives in the CALLING process's DPI space) into
    /// client coordinates in the delivery chain's physical space, using the target window's
    /// rect measured in BOTH spaces: the ratio between them maps one space onto the other
    /// deterministically. Last-resort path for virtual elements that neither own a native
    /// window nor support the Invoke pattern.
    /// </summary>
    private static (int X, int Y) UiaRectToPhysicalClient(nint hWnd, DesktopAutomation.ElementInfo element)
    {
        var (callerX, callerY, callerWidth, callerHeight) = DesktopWin32.WindowBounds(hWnd);
        using var dpi = DesktopWin32.PerMonitorDpiScope.Enter();
        var (physicalX, physicalY, physicalWidth, physicalHeight) = DesktopWin32.WindowBounds(hWnd);
        var ratioX = callerWidth > 0 ? physicalWidth / (double)callerWidth : 1;
        var ratioY = callerHeight > 0 ? physicalHeight / (double)callerHeight : 1;
        var screenX = (int)Math.Round(physicalX + (element.CenterX - callerX) * ratioX);
        var screenY = (int)Math.Round(physicalY + (element.CenterY - callerY) * ratioY);
        var client = DesktopWin32.ScreenToClient(hWnd, screenX, screenY);
        return (client.X, client.Y);
    }

    /// <summary>The delivery half of a click, unchanged from the pre-compensation era so the
    /// default path behaves exactly as before: isolated posts on the isolated desktop, fast
    /// sends real input after focusing, silent posts to the resolved receiver.</summary>
    private async Task<DesktopToolResult> DeliverClickAsync(DesktopClickArgs args, string mode, nint hWnd, string button, int count)
    {
        if (mode == IsolatedMode)
        {
            if (hWnd == 0)
                return Fail("desktop_window_required", "isolated mode delivers input as window messages: window_id is required.", permanent: true);
            await InputGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var isolated = DesktopWin32.RunOnIsolatedDesktop(() => ClickPosted(hWnd, args.X!.Value, args.Y!.Value, button, count), out var error);
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
            var (screenX, screenY) = ResolveScreenPoint(hWnd, args.X!.Value, args.Y!.Value);
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

    // ── coordinate compensation ────────────────────────────────────────────────

    /// <summary>The model/protocol identity the host declared for compensation. The host owns
    /// this because only it knows which chat model is producing coordinates; a wrong
    /// declaration is bounded by the post-click semantic verification, which is the backstop
    /// every compensated click runs.</summary>
    internal sealed record CoordinateCompensationConfig(string Model, string Protocol);

    /// <summary>
    /// Resolves whether the compensation layer is on. Precedence mirrors mode resolution:
    /// the frozen execution-context setting desktop.coordinate_compensation
    /// {enabled, model, protocol}, then TINADEC_HOME/config/computer-use.json's
    /// coordinate_compensation object. Absent or enabled=false means off; anything malformed
    /// throws, because an enabled-but-unreadable compensation must never fall back to raw
    /// coordinates that silently click the wrong place.
    /// </summary>
    internal CoordinateCompensationConfig? ResolveCoordinateCompensation()
    {
        var setting = ToolExecutionContext.Current?.Setting("desktop", "coordinate_compensation");
        if (setting is { ValueKind: JsonValueKind.Object } node)
            return ParseCompensationSection(node, "execution-context setting desktop.coordinate_compensation");
        if (setting is not null && setting.Value.ValueKind is not JsonValueKind.Null)
            throw new DesktopConfigurationException("desktop_config_invalid: execution-context setting desktop.coordinate_compensation must be an object {enabled, model, protocol}.");

        var home = _homeProvider();
        if (string.IsNullOrWhiteSpace(home)) return null;
        var file = Path.Combine(home, "config", "computer-use.json");
        if (!File.Exists(file)) return null;
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            root = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new DesktopConfigurationException($"desktop_config_invalid: {file} is not valid JSON ({exception.Message}).");
        }
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("coordinate_compensation", out var section) || section.ValueKind is JsonValueKind.Null)
            return null;
        if (section.ValueKind != JsonValueKind.Object)
            throw new DesktopConfigurationException($"desktop_config_invalid: coordinate_compensation in {file} must be an object {{enabled, model, protocol}}.");
        return ParseCompensationSection(section, $"coordinate_compensation in {file}");
    }

    private static CoordinateCompensationConfig? ParseCompensationSection(JsonElement node, string source)
    {
        if (!node.TryGetProperty("enabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new DesktopConfigurationException($"desktop_config_invalid: {source}.enabled must be a boolean.");
        if (enabled.ValueKind == JsonValueKind.False) return null;
        var model = node.TryGetProperty("model", out var modelNode) && modelNode.ValueKind == JsonValueKind.String ? modelNode.GetString()!.Trim() : null;
        var protocol = node.TryGetProperty("protocol", out var protocolNode) && protocolNode.ValueKind == JsonValueKind.String ? protocolNode.GetString()!.Trim() : null;
        if (string.IsNullOrEmpty(model) || string.IsNullOrEmpty(protocol))
            throw new DesktopConfigurationException($"desktop_config_invalid: {source} is enabled and needs non-empty model and protocol (the model whose coordinates are being compensated, and the wire protocol it speaks).");
        return new CoordinateCompensationConfig(model!, protocol!);
    }

    /// <summary>
    /// Maps the reported coordinates onto the click target. The chain is:
    /// reported → shown image (the ctx copy the model actually saw, × the measured profile
    /// scale) → full capture (× full/context, 1 when no downscale happened) → client area
    /// (minus the non-client frame the capture includes). Every failure is a refusal, never
    /// a guess: no screenshot on record, a click on a window other than the captured one, or
    /// no measured profile row for this model and image shape.
    /// </summary>
    private (DesktopToolResult? Error, CompensationPlan? Plan) PlanCoordinateCompensation(CoordinateCompensationConfig config, DesktopClickArgs args)
    {
        ServedCapture? capture;
        lock (_captureLock) _lastCapture.TryGetValue(ToolExecutionContext.Current?.RunId ?? "", out capture);
        if (capture is null)
            return (Fail("desktop_capture_context_missing",
                "coordinate compensation is enabled, but this run has no desktop_screenshot on record. Take a desktop_screenshot of the target first: its image is the coordinate space your x/y are read against.",
                permanent: true), null);

        var clickedWindow = args.WindowId;
        if ((clickedWindow ?? 0) != (capture.WindowId ?? 0))
            return (Fail("desktop_compensation_target_mismatch",
                $"coordinate compensation is bound to the last captured target (window_id {capture.WindowId ?? 0}), but the click targets window_id {clickedWindow ?? 0}. Click the window you captured, or take a fresh desktop_screenshot of the new target.",
                permanent: true), null);

        var store = CoordinateProfileStore.Load(_homeProvider());
        var aspect = capture.ContextWidth / (double)capture.ContextHeight;
        var shortEdge = Math.Min(capture.ContextWidth, capture.ContextHeight);
        var row = store.Find(config.Model, config.Protocol, aspect, shortEdge);
        if (row is null)
            return (Fail("desktop_coordinate_profile_missing",
                $"no measured coordinate profile for model '{config.Model}' protocol '{config.Protocol}' covering aspect {aspect:0.##} (short edge {shortEdge}). "
                + $"{store.DescribeCoverage(config.Model, config.Protocol)} Measure the missing shape with the clickloop harness (--size=WxH) and add the row to {store.Path}; unmeasured shapes refuse to click rather than guess a scale.",
                permanent: true), null);

        var imageX = (int)Math.Round(args.X!.Value * row.ScaleX * (capture.FullWidth / (double)capture.ContextWidth));
        var imageY = (int)Math.Round(args.Y!.Value * row.ScaleY * (capture.FullHeight / (double)capture.ContextHeight));
        int clientX, clientY;
        if (clickedWindow is { } id)
        {
            var (offsetX, offsetY) = ClientOriginInImage((nint)id);
            clientX = imageX - offsetX;
            clientY = imageY - offsetY;
        }
        else
        {
            // a screen capture has no frame: image space is the virtual screen itself
            clientX = imageX;
            clientY = imageY;
        }

        Logger.Info(
            "desktop_click compensation: {Model}/{Protocol} aspect {Aspect:0.##} short edge {Short} scaled ({FromX},{FromY}) -> ({ToX},{ToY}) via {Profile}",
            config.Model, config.Protocol, aspect, shortEdge, args.X.Value, args.Y.Value, clientX, clientY, store.Path);

        var compensated = new DesktopClickArgs
        {
            X = clientX, Y = clientY, WindowId = args.WindowId, Button = args.Button, Count = args.Count, Mode = args.Mode,
        };
        var info = new DesktopCompensationInfo
        {
            Model = row.Model,
            Protocol = row.Protocol,
            ScaleX = row.ScaleX,
            ScaleY = row.ScaleY,
            From = new DesktopPointInfo { X = args.X.Value, Y = args.Y.Value },
            To = new DesktopPointInfo { X = clientX, Y = clientY },
        };
        return (null, new CompensationPlan(compensated, info, capture));
    }

    private sealed record CompensationPlan(DesktopClickArgs Args, DesktopCompensationInfo Info, ServedCapture Capture);

    /// <summary>
    /// Where the client area starts inside a window capture: the capture includes the
    /// non-client frame, so a point computed in image space must shift by this to become a
    /// client point. Measured under the same PMv2 scope the capture used, so both are physical.
    /// </summary>
    private static (int X, int Y) ClientOriginInImage(nint hWnd)
    {
        using var dpi = DesktopWin32.PerMonitorDpiScope.Enter();
        var (x, y, _, _) = DesktopWin32.WindowBounds(hWnd);
        var client = DesktopWin32.ClientToScreen(hWnd, 0, 0);
        return (client.X - x, client.Y - y);
    }

    // ── semantic verification of compensated clicks ─────────────────────────────

    internal const string VerificationVerified = "verified";
    internal const string VerificationFailed = "failed";
    internal const string VerificationUnverified = "unverified";

    private DesktopTextInfo[]? ReadTextsForVerification(nint hWnd, string mode)
    {
        var read = ReadText(new DesktopReadTextArgs { WindowId = hWnd, Mode = mode, MaxChars = 4096 });
        return read.Ok ? read.Texts : null;
    }

    /// <summary>
    /// Judges a compensated or element click by the application's own words: the window's
    /// texts were read before the click, and are polled after it (posted input is
    /// asynchronous). STABLE-CONTROL RULE: only CHILD CONTROL text counts as click evidence.
    /// A window title is not a stable verification source - measured twice, once as garbage
    /// from uninitialized memory (round 8) and once as per-read churn (round 9) - so a
    /// title-only change reports unverified, never verified. Child change = verified;
    /// children readable but unchanged = failed (a miss, or a click with no text-level
    /// effect - the screenshots say which); nothing readable / unstable source = unverified.
    /// </summary>
    private DesktopVerificationInfo VerifyAfterClick(nint hWnd, string mode, DesktopTextInfo[]? beforeTexts, ServedCapture capture)
    {
        DesktopVerificationInfo Verdict(string status, string message)
        {
            var afterShot = hWnd != 0
                ? Screenshot(new DesktopScreenshotArgs { WindowId = hWnd, Mode = mode })
                : null;
            return new DesktopVerificationInfo
            {
                Status = status,
                BeforePath = string.IsNullOrEmpty(capture.ContextPath) ? null : capture.ContextPath,
                AfterPath = afterShot is { Ok: true } ? afterShot.Path : null,
                Message = message,
            };
        }

        if (hWnd == 0)
            return Verdict(VerificationUnverified,
                "click delivered at screen coordinates; there is no single window to read text from, so the click is unverified. Take a desktop_screenshot to check what changed.");
        if (beforeTexts is null || beforeTexts.Length == 0)
            return Verdict(VerificationUnverified,
                "click delivered, but unverified: the target window has no readable text, so the click cannot be judged semantically. Inspect the after screenshot before assuming the click landed.");

        var beforeChildren = beforeTexts.Where(t => t.WindowId != hWnd).ToArray();
        var beforeRoot = beforeTexts.FirstOrDefault(t => t.WindowId == hWnd)?.Text;
        if (beforeChildren.Length == 0)
            return Verdict(VerificationUnverified,
                "click delivered, but unverified: only the window title is readable, and a title is not a stable verification source. Use desktop_find_element / desktop_read_text on the target's controls, or inspect the after screenshot.");

        // posted input settles asynchronously: poll for a child-text change instead of
        // reading once and racing the message pump. 24 x 50ms bounds a no-op click at
        // ~1.2s while giving a loaded message pump room to process the click. A detected
        // change must also CONFIRM: re-read once and require the new texts to be stable -
        // per-read churn (a live-updating control) is not click evidence either.
        var sawChurningText = false;
        string? lastRoot = beforeRoot;
        for (var attempt = 0; attempt < 24; attempt++)
        {
            var after = ReadTextsForVerification(hWnd, mode);
            if (after is not null)
            {
                lastRoot = after.FirstOrDefault(t => t.WindowId == hWnd)?.Text ?? lastRoot;
                var afterChildren = after.Where(t => t.WindowId != hWnd).ToArray();
                if (TextsChanged(beforeChildren, afterChildren))
                {
                    var confirm = ReadTextsForVerification(hWnd, mode);
                    var confirmChildren = confirm?.Where(t => t.WindowId != hWnd).ToArray();
                    if (confirmChildren is not null && SameTexts(afterChildren, confirmChildren))
                        return Verdict(VerificationVerified,
                            "click delivered and verified: the target window's controls changed their text, so the application responded.");
                    sawChurningText = true;
                }
            }
            Thread.Sleep(50);
        }
        if (sawChurningText)
            return Verdict(VerificationUnverified,
                "click delivered, but unverified: the window's text kept changing between reads (an unstable text source, e.g. a live-updating control), so the click cannot be judged from text. Inspect the after screenshot before assuming the click landed.");
        if (!string.Equals(beforeRoot, lastRoot, StringComparison.Ordinal))
            return Verdict(VerificationUnverified,
                "click delivered, but unverified: only the window title changed, and a title is not a stable verification source (it changes for unrelated reasons - clocks, unsaved-state markers). Inspect the after screenshot or verify a control's text instead.");
        return Verdict(VerificationFailed,
            "click delivered but NOT verified: the window's readable control text did not change. Either the click missed, or it hit something that produces no text change. Compare the before/after screenshots before retrying with adjusted coordinates.");
    }

    private static bool TextsChanged(DesktopTextInfo[] before, DesktopTextInfo[] after)
    {
        if (before.Length != after.Length) return true;
        var beforeTexts = before.ToDictionary(t => t.WindowId, t => t.Text ?? "");
        foreach (var item in after)
            if (!beforeTexts.TryGetValue(item.WindowId, out var text) || !string.Equals(text, item.Text ?? "", StringComparison.Ordinal))
                return true;
        return false;
    }

    private static bool SameTexts(DesktopTextInfo[] left, DesktopTextInfo[] right)
    {
        if (left.Length != right.Length) return false;
        var leftTexts = left.ToDictionary(t => t.WindowId, t => t.Text ?? "");
        foreach (var item in right)
            if (!leftTexts.TryGetValue(item.WindowId, out var text) || !string.Equals(text, item.Text ?? "", StringComparison.Ordinal))
                return false;
        return true;
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

        // ── element typing: the Value pattern sets the text semantically (no keyboard
        // input, no focus steal, works for WPF/UWP documents); a native edit window falls
        // back to WM_CHAR straight into the element's own window ─────────────────────
        if (args.ElementIndex is not null || args.ElementAutomationId is not null)
        {
            var (elementError, element, raw, elementIndex, elementWindow) = ResolveClickElement(new DesktopClickArgs { ElementIndex = args.ElementIndex, ElementAutomationId = args.ElementAutomationId, WindowId = args.WindowId });
            if (elementError is not null) return elementError;
            DesktopToolResult elementResult;
            string delivery;
            var valueSet = false;
            try
            {
                valueSet = DesktopAutomation.TrySetValue(raw!, args.Text);
            }
            catch (COMException)
            {
                valueSet = false;
            }
            if (valueSet)
            {
                delivery = "uia-value";
                elementResult = new DesktopToolResult { Ok = true, Applied = true, Mode = mode, WindowId = elementWindow, Chars = args.Text.Length };
            }
            else if (element!.NativeWindowHandle != 0)
            {
                delivery = "native-post";
                await InputGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (mode == IsolatedMode)
                    {
                        var isolated = DesktopWin32.RunOnIsolatedDesktop(() => PostCharsToNativeElement(element.NativeWindowHandle, args.Text), out var runError);
                        elementResult = runError is not null ? Fail("desktop_isolated_failed", runError, permanent: false) : isolated;
                    }
                    else
                    {
                        elementResult = PostCharsToNativeElement(element.NativeWindowHandle, args.Text);
                    }
                }
                finally
                {
                    InputGate.Release();
                }
            }
            else
            {
                return Fail("desktop_element_not_typeable",
                    "the element supports neither the Value pattern (it is not a text element, or is read-only) nor a native edit window to type into. Use desktop_press_key on a focused element, or pick the actual text field with desktop_find_element.",
                    permanent: false);
            }
            elementResult.Element = ToElementInfo(element, elementIndex);
            // the native-post path may itself have substituted a clipboard paste (emoji
            // into a classic Edit control) - keep the more specific delivery it reports
            if (delivery != "native-post" || elementResult.Delivery is not { } inner || inner == "wm-char")
                elementResult.Delivery = delivery;
            return elementResult;
        }

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
            var delivery = DesktopWin32.PostText(receiver, args.Text);
            return new DesktopToolResult { Ok = true, Applied = true, Mode = mode, Chars = args.Text.Length, WindowId = receiver, Delivery = delivery };
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
        var delivery = DesktopWin32.PostText(receiver, text);
        return new DesktopToolResult { Ok = true, Applied = true, Mode = IsolatedMode, Chars = text.Length, WindowId = receiver, Delivery = delivery };
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

        // the shape of THIS image is what coordinate compensation must map onto, so it is
        // remembered per run at the moment it is served
        RememberCapture(new ServedCapture(window?.WindowId, width, height, contextWidth, contextHeight, contextPath));

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
