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
        if (args.X is null || args.Y is null) return Fail("desktop_input_failed", "desktop_click needs integer x and y.", permanent: true);
        var button = args.Button ?? "left";
        if (button is not ("left" or "right" or "middle"))
            return Fail("desktop_input_failed", "button must be left, right or middle.", permanent: true);
        var count = Math.Clamp(args.Count ?? 1, 1, 3);
        var hWnd = args.WindowId is { } value ? (nint)value : 0;
        if (hWnd != 0 && !IsAliveWindow(hWnd))
            return Fail("desktop_window_not_found", $"No window with window_id {args.WindowId}.", permanent: true);

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
            // pre-click text baseline: the verdict after the click compares against this
            if (hWnd != 0) beforeTexts = ReadTextsForVerification(hWnd, mode);
        }

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
    /// Judges a compensated click by the application's own words: the window's texts were
    /// read before the click, and are polled after it (posted input is asynchronous).
    /// Text change = verified; readable but unchanged = failed (a miss, or a click with no
    /// text-level effect - the screenshots say which); nothing readable = unverified, which
    /// is reported as exactly that. A fresh after-capture is saved either way so the caller
    /// can see what actually happened.
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
                BeforePath = capture.ContextPath,
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

        // posted input settles asynchronously: poll for a text change instead of reading
        // once and racing the message pump. 24 x 50ms bounds a no-op click at ~1.2s while
        // giving a loaded message pump room to process the click before the verdict.
        // A detected change must also CONFIRM: re-read once and require the new texts to be
        // stable. A text source that differs on every read (measured: our own harness window's
        // title marshalled into uninitialized memory; production analog: a ticking clock title)
        // would otherwise turn every click - hit or miss - into a false `verified`. Real
        // click effects persist; per-read noise keeps churning and is reported as unverified.
        var sawChurningText = false;
        for (var attempt = 0; attempt < 24; attempt++)
        {
            var after = ReadTextsForVerification(hWnd, mode);
            if (after is not null && TextsChanged(beforeTexts, after))
            {
                var confirm = ReadTextsForVerification(hWnd, mode);
                if (confirm is not null && SameTexts(after, confirm))
                    return Verdict(VerificationVerified,
                        "click delivered and verified: the target window's text changed, so the application responded.");
                sawChurningText = true;
            }
            Thread.Sleep(50);
        }
        if (sawChurningText)
            return Verdict(VerificationUnverified,
                "click delivered, but unverified: the window's text kept changing between reads (an unstable text source, e.g. a live-updating title), so the click cannot be judged from text. Inspect the after screenshot before assuming the click landed.");
        return Verdict(VerificationFailed,
            "click delivered but NOT verified: the window's readable text did not change. Either the click missed, or it hit something that produces no text change. Compare the before/after screenshots before retrying with adjusted coordinates.");
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
