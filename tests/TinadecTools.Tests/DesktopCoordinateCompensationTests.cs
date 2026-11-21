using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using TinadecTools.Tools.Desktop;

namespace TinadecTools.Tests;

/// <summary>
/// Coordinate-compensation tests. The profile rows here are synthetic (clean numbers like a
/// scale of exactly 2) so the math is asserted exactly; the real measured rows for production
/// models live in the design doc and in TINADEC_HOME/config/coordinate-profiles.json on the
/// machines that enable the layer. Every fail-closed path must refuse the click BEFORE any
/// input is delivered - the button windows count their own clicks, so a refused click is
/// provable: the count stays zero.
///
/// The windows live on the TinadecIsolated desktop (the clickloop harness pattern): the
/// user's desktop is full of foreign windows that steal WindowFromPoint hit-tests, while the
/// isolated desktop holds only what the test put there. The whole chain - isolated capture,
/// compensation, isolated message-direct click, isolated read-back - is the same one the
/// real-machine measurements exercised.
/// </summary>
public sealed class DesktopCoordinateCompensationTests
{
    private static string TempHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "tinadec-desktop-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, "config"));
        return home;
    }

    private static void EnableCompensation(string home, string model = "test-model", string protocol = "test-protocol") =>
        File.WriteAllText(Path.Combine(home, "config", "computer-use.json"),
            "{\"default_mode\": \"isolated\", \"coordinate_compensation\": {\"enabled\": true, \"model\": \"" + model
            + "\", \"protocol\": \"" + protocol + "\"}}");

    private static void WriteProfiles(string home, string json) =>
        File.WriteAllText(Path.Combine(home, "config", "coordinate-profiles.json"), json);

    private const string CoveringProfile =
        """{"schema_version": 1, "profiles": [{"model": "test-model", "protocol": "test-protocol", "aspect_min": 0.5, "aspect_max": 3.6, "short_edge_min": 100, "short_edge_max": 1600, "scale_x": 2.0, "scale_y": 2.0, "samples": 30, "hit_rate": 1.0, "tested": "2026-11-05"}]}""";

    // ── off by default: nothing about the result shape changes ─────────────────

    [Fact]
    public async Task Compensation_OffByDefault_ClickIsUnchanged()
    {
        var runtime = new DesktopToolRuntime(() => TempHome());
        using var window = new ButtonWindow();
        var shot = window.Screenshot(runtime);
        Assert.True(shot.Ok, shot.Message);

        var click = await runtime.ClickAsync(new DesktopClickArgs
        {
            X = 120, Y = 50, WindowId = window.Hwnd, Mode = DesktopToolRuntime.IsolatedMode,
        });
        Assert.True(click.Ok, click.Message);
        Assert.Null(click.Compensation);
        Assert.Null(click.Verification);
        window.AssertClicks(1);
    }

    // ── fail-closed: every unmeasured or malformed path refuses before clicking ─

    [Fact]
    public async Task Compensation_EnabledWithoutProfileStoreFailsClosed()
    {
        var home = TempHome();
        EnableCompensation(home);
        var runtime = new DesktopToolRuntime(() => home);
        using var window = new ButtonWindow();
        Assert.True(window.Screenshot(runtime).Ok);

        await Assert.ThrowsAsync<DesktopToolRuntime.DesktopConfigurationException>(() =>
            runtime.ClickAsync(new DesktopClickArgs { X = 120, Y = 50, WindowId = window.Hwnd, Mode = DesktopToolRuntime.IsolatedMode }));
        window.AssertClicks(0);
    }

    [Fact]
    public async Task Compensation_MalformedProfileStoreFailsClosed()
    {
        var home = TempHome();
        EnableCompensation(home);
        WriteProfiles(home, "{ not json");
        var runtime = new DesktopToolRuntime(() => home);
        using var window = new ButtonWindow();
        Assert.True(window.Screenshot(runtime).Ok);

        await Assert.ThrowsAsync<DesktopToolRuntime.DesktopConfigurationException>(() =>
            runtime.ClickAsync(new DesktopClickArgs { X = 120, Y = 50, WindowId = window.Hwnd, Mode = DesktopToolRuntime.IsolatedMode }));
        window.AssertClicks(0);
    }

    /// <summary>The profile-required assertion: a model that was never measured must refuse
    /// the click with an error that says what is missing and how to measure it.</summary>
    [Fact]
    public async Task Compensation_UnmeasuredModelIsRefusedWithGuidance()
    {
        var home = TempHome();
        EnableCompensation(home, model: "never-measured-model");
        WriteProfiles(home, CoveringProfile);
        var runtime = new DesktopToolRuntime(() => home);
        using var window = new ButtonWindow();
        Assert.True(window.Screenshot(runtime).Ok);

        var result = await runtime.ClickAsync(new DesktopClickArgs
        {
            X = 120, Y = 50, WindowId = window.Hwnd, Mode = DesktopToolRuntime.IsolatedMode,
        });
        Assert.False(result.Ok);
        Assert.Equal("desktop_coordinate_profile_missing", result.Code);
        Assert.Contains("never-measured-model", result.Message, StringComparison.Ordinal);
        Assert.Contains("clickloop", result.Message, StringComparison.Ordinal);
        Assert.Null(result.Compensation);
        window.AssertClicks(0);
    }

    [Fact]
    public async Task Compensation_UnmeasuredImageShapeIsRefused()
    {
        var home = TempHome();
        EnableCompensation(home);
        // the row covers aspect 0.5–1.6 only; a wide window's capture is ~2.1
        WriteProfiles(home,
            """{"schema_version": 1, "profiles": [{"model": "test-model", "protocol": "test-protocol", "aspect_min": 0.5, "aspect_max": 1.6, "short_edge_min": 100, "short_edge_max": 1600, "scale_x": 2.0, "scale_y": 2.0, "samples": 30, "hit_rate": 1.0, "tested": "2026-11-05"}]}""");
        var runtime = new DesktopToolRuntime(() => home);
        using var window = new ButtonWindow(width: 900, height: 400);
        var shot = window.Screenshot(runtime);
        Assert.True(shot.Ok, shot.Message);
        Assert.True(shot.Width!.Value / (double)shot.Height!.Value > 1.6, $"expected a wide capture, got {shot.Width}x{shot.Height}");

        var result = await runtime.ClickAsync(new DesktopClickArgs
        {
            X = 120, Y = 50, WindowId = window.Hwnd, Mode = DesktopToolRuntime.IsolatedMode,
        });
        Assert.False(result.Ok);
        Assert.Equal("desktop_coordinate_profile_missing", result.Code);
        Assert.Contains("aspect", result.Message, StringComparison.Ordinal);
        window.AssertClicks(0);
    }

    [Fact]
    public async Task Compensation_ClickWithoutScreenshotIsRefused()
    {
        var home = TempHome();
        EnableCompensation(home);
        WriteProfiles(home, CoveringProfile);
        var runtime = new DesktopToolRuntime(() => home);
        using var window = new ButtonWindow();

        var result = await runtime.ClickAsync(new DesktopClickArgs
        {
            X = 120, Y = 50, WindowId = window.Hwnd, Mode = DesktopToolRuntime.IsolatedMode,
        });
        Assert.False(result.Ok);
        Assert.Equal("desktop_capture_context_missing", result.Code);
        window.AssertClicks(0);
    }

    [Fact]
    public async Task Compensation_ClickOnUncapturedWindowIsRefused()
    {
        var home = TempHome();
        EnableCompensation(home);
        WriteProfiles(home, CoveringProfile);
        var runtime = new DesktopToolRuntime(() => home);
        using var captured = new ButtonWindow();
        using var other = new ButtonWindow(x: 500);
        Assert.True(captured.Screenshot(runtime).Ok);

        // a different window than the captured one
        var result = await runtime.ClickAsync(new DesktopClickArgs
        {
            X = 120, Y = 50, WindowId = other.Hwnd, Mode = DesktopToolRuntime.IsolatedMode,
        });
        Assert.False(result.Ok);
        Assert.Equal("desktop_compensation_target_mismatch", result.Code);
        other.AssertClicks(0);

        // and a window-less click while a window capture is on record
        var screen = await runtime.ClickAsync(new DesktopClickArgs
        {
            X = 120, Y = 50, Mode = DesktopToolRuntime.SilentMode,
        });
        Assert.False(screen.Ok);
        Assert.Equal("desktop_compensation_target_mismatch", screen.Code);
        other.AssertClicks(0);
    }

    // ── the mapping itself: reported coords land where the profile says ─────────

    /// <summary>
    /// The button's center, expressed in the model's reported space (half the image, scale 2),
    /// must land back on the button after compensation - and the click must come back
    /// `verified` because the button's label carries the click count.
    /// </summary>
    [Fact]
    public async Task Compensation_MapsReportedCoordsOntoTheButtonAndVerifies()
    {
        var home = TempHome();
        EnableCompensation(home);
        WriteProfiles(home, CoveringProfile);
        var runtime = new DesktopToolRuntime(() => home);
        using var window = new ButtonWindow();
        var shot = window.Screenshot(runtime);
        Assert.True(shot.Ok, shot.Message);

        // served-capture bookkeeping: the record is the ctx copy the model saw
        var record = runtime.LastServedCapture();
        Assert.NotNull(record);
        Assert.Equal(window.Hwnd, record!.WindowId);
        Assert.Equal(shot.Image!.Width, record.ContextWidth);
        Assert.Equal(shot.Image!.Height, record.ContextHeight);
        Assert.Equal(shot.Image.Path, record.ContextPath);

        var (modelX, modelY) = window.ModelCoordsForClientPoint(120, 50, scale: 2.0, shot);
        var click = await runtime.ClickAsync(new DesktopClickArgs
        {
            X = modelX, Y = modelY, WindowId = window.Hwnd, Mode = DesktopToolRuntime.IsolatedMode,
        });

        Assert.True(click.Ok, click.Message);
        window.AssertClicks(1);
        Assert.NotNull(click.Compensation);
        Assert.Equal(2.0, click.Compensation!.ScaleX);
        Assert.Equal(modelX, click.Compensation.From!.X);
        Assert.Equal(modelY, click.Compensation.From!.Y);
        // the compensated point is inside the button's client rect
        var (toX, toY) = (click.Compensation.To!.X, click.Compensation.To!.Y);
        Assert.True(toX >= 20 && toX <= 220 && toY >= 20 && toY <= 80, $"compensated point ({toX},{toY}) is not on the button");
        Assert.NotNull(click.Verification);
        Assert.Equal(DesktopToolRuntime.VerificationVerified, click.Verification!.Status);
        Assert.Equal(shot.Image.Path, click.Verification.BeforePath);
        Assert.True(File.Exists(click.Verification.AfterPath!), "after screenshot missing");
        Assert.Contains("verified", click.Message, StringComparison.Ordinal);
    }

    /// <summary>A capture above the context cap is downscaled; the profile applies in the
    /// ctx space the model saw, so the mapping must scale through full/ctx and still land.</summary>
    [Fact]
    public async Task Compensation_DownscaledCaptureScalesThroughTheContextCopy()
    {
        var home = TempHome();
        EnableCompensation(home);
        WriteProfiles(home, CoveringProfile);
        var runtime = new DesktopToolRuntime(() => home);
        using var window = new ButtonWindow(width: 1700, height: 600);
        var shot = window.Screenshot(runtime);
        Assert.True(shot.Ok, shot.Message);
        Assert.True(shot.Width > DesktopToolRuntime.MaxContextImageEdge, "expected a capture above the context cap");
        Assert.True(shot.Image!.Width <= DesktopToolRuntime.MaxContextImageEdge);

        var (modelX, modelY) = window.ModelCoordsForClientPoint(120, 50, scale: 2.0, shot);
        var click = await runtime.ClickAsync(new DesktopClickArgs
        {
            X = modelX, Y = modelY, WindowId = window.Hwnd, Mode = DesktopToolRuntime.IsolatedMode,
        });

        Assert.True(click.Ok, click.Message);
        window.AssertClicks(1);
        Assert.Equal(DesktopToolRuntime.VerificationVerified, click.Verification!.Status);
    }

    [Fact]
    public async Task Compensation_DeadSpaceClickReportsFailedVerification()
    {
        var home = TempHome();
        EnableCompensation(home);
        WriteProfiles(home, CoveringProfile);
        var runtime = new DesktopToolRuntime(() => home);
        // taller window so the dead space is unambiguous: probe-measured, a click at client
        // (160,300) resolves to the window background, never the button at the top-left
        using var window = new ButtonWindow(width: 320, height: 500);
        var shot = window.Screenshot(runtime);
        Assert.True(shot.Ok, shot.Message);

        // client dead space far below the button: delivered, but nothing changes any text
        var (modelX, modelY) = window.ModelCoordsForClientPoint(160, 300, scale: 2.0, shot);
        var click = await runtime.ClickAsync(new DesktopClickArgs
        {
            X = modelX, Y = modelY, WindowId = window.Hwnd, Mode = DesktopToolRuntime.IsolatedMode,
        });

        Assert.True(click.Ok, click.Message);
        window.AssertClicks(0);
        Assert.Equal(DesktopToolRuntime.VerificationFailed, click.Verification!.Status);
        Assert.NotNull(click.Verification.BeforePath);
        Assert.True(File.Exists(click.Verification.AfterPath!));
        Assert.Contains("NOT verified", click.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compensation_TextlessWindowReportsUnverified()
    {
        var home = TempHome();
        EnableCompensation(home);
        WriteProfiles(home, CoveringProfile);
        var runtime = new DesktopToolRuntime(() => home);
        using var window = new PlainWindow();
        var shot = window.Screenshot(runtime);
        Assert.True(shot.Ok, shot.Message);

        var (modelX, modelY) = window.ModelCoordsForClientPoint(150, 90, scale: 2.0, shot);
        var click = await runtime.ClickAsync(new DesktopClickArgs
        {
            X = modelX, Y = modelY, WindowId = window.Hwnd, Mode = DesktopToolRuntime.IsolatedMode,
        });

        Assert.True(click.Ok, click.Message);
        Assert.Equal(DesktopToolRuntime.VerificationUnverified, click.Verification!.Status);
        Assert.True(File.Exists(click.Verification.AfterPath!));
        Assert.Contains("unverified", click.Verification.Message, StringComparison.Ordinal);
    }

    /// <summary>An unstable text source (per-read churning text) must not be mistaken for a
    /// click effect: the verdict is `unverified`, never a false `verified` - and churn does
    /// not count toward the circuit breaker either (it is not a failed verification).</summary>
    [Fact]
    public async Task Compensation_ChurningTextIsUnverifiedNotVerified()
    {
        var home = TempHome();
        EnableCompensation(home);
        WriteProfiles(home, CoveringProfile);
        var runtime = new DesktopToolRuntime(() => home);
        using var window = new ChurnWindow();
        var shot = window.Screenshot(runtime);
        Assert.True(shot.Ok, shot.Message);

        var (modelX, modelY) = window.ModelCoordsForClientPoint(150, 90, scale: 2.0, shot);
        var click = await runtime.ClickAsync(new DesktopClickArgs
        {
            X = modelX, Y = modelY, WindowId = window.Hwnd, Mode = DesktopToolRuntime.IsolatedMode,
        });

        Assert.True(click.Ok, click.Message);
        Assert.Equal(DesktopToolRuntime.VerificationUnverified, click.Verification!.Status);
        Assert.Contains("title", click.Verification.Message, StringComparison.Ordinal);

        // churn is `unverified`, not `failed`: a follow-up compensated click still executes
        var again = await runtime.ClickAsync(new DesktopClickArgs
        {
            X = modelX, Y = modelY, WindowId = window.Hwnd, Mode = DesktopToolRuntime.IsolatedMode,
        });
        Assert.True(again.Ok, again.Message);
        Assert.Equal(DesktopToolRuntime.VerificationUnverified, again.Verification!.Status);
    }

    // ── element finding and element clicking (the precise path) ────────────────

    [Fact]
    public async Task FindElement_ReturnsFilteredElementsWithPhysicalBounds()
    {
        var runtime = new DesktopToolRuntime(() => TempHome());
        using var window = new ElementsWindow();

        var byName = runtime.FindElement(new DesktopFindElementArgs { WindowId = window.Hwnd, Name = "save", Mode = DesktopToolRuntime.IsolatedMode });
        Assert.True(byName.Ok, byName.Message);
        Assert.Equal(1, byName.Total);
        var save = Assert.Single(byName.Elements!);
        Assert.Equal("Save", save.Name);
        Assert.Equal("Button", save.ControlType);
        Assert.NotNull(save.Bounds);
        Assert.True(save.Bounds!.Width > 0 && save.Bounds.Height > 0);
        // the bounds are in the caller's DPI space, so compare against the window rect in
        // that same space (NO PMv2 scope here - mixing the two spaces moves everything)
        {
            var (wx, wy, ww, wh) = DesktopWin32.WindowBounds(window.Hwnd);
            Assert.True(save.Bounds.X >= wx && save.Bounds.X + save.Bounds.Width <= wx + ww, $"save bounds {save.Bounds.X}..{save.Bounds.X + save.Bounds.Width} outside window {wx}..{wx + ww}");
            Assert.True(save.Bounds.Y >= wy && save.Bounds.Y + save.Bounds.Height <= wy + wh);
        }

        var buttons = runtime.FindElement(new DesktopFindElementArgs { WindowId = window.Hwnd, ControlType = "button", Mode = DesktopToolRuntime.IsolatedMode });
        Assert.True(buttons.Ok, buttons.Message);
        // the 4 app buttons (Save/Cancel/Tiny/Clipped) plus whatever caption buttons the
        // OS exposes - the caption count is NOT stable (Win11 adds an overflow button
        // depending on layout), so assert the app buttons, not a total
        var appButtons = buttons.Elements!.Where(element => element.Name is "Save" or "Cancel" or "Tiny" or "Clipped").ToList();
        Assert.Equal(4, appButtons.Count);

        var unknownType = runtime.FindElement(new DesktopFindElementArgs { WindowId = window.Hwnd, ControlType = "rocket", Mode = DesktopToolRuntime.IsolatedMode });
        Assert.False(unknownType.Ok);
        Assert.Equal("desktop_uia_failed", unknownType.Code);
    }

    /// <summary>The money test of this round: a 16x16 target, clicked through its UIA
    /// rectangle with no coordinates anywhere - the small-target problem solved.</summary>
    [Fact]
    public async Task ClickByElement_LandsOnASixteenPixelTargetAndVerifies()
    {
        var runtime = new DesktopToolRuntime(() => TempHome());
        using var window = new ElementsWindow();

        var found = runtime.FindElement(new DesktopFindElementArgs { WindowId = window.Hwnd, Name = "Tiny", Mode = DesktopToolRuntime.IsolatedMode });
        Assert.True(found.Ok, found.Message);
        var tiny = Assert.Single(found.Elements!);
        var tinyIndex = tiny.Index;
        Assert.True(tiny.Bounds!.Width <= 24 && tiny.Bounds.Height <= 24, $"expected a tiny target, got {tiny.Bounds.Width}x{tiny.Bounds.Height}");

        var click = await runtime.ClickAsync(new DesktopClickArgs
        {
            ElementIndex = tinyIndex, WindowId = window.Hwnd, Mode = DesktopToolRuntime.IsolatedMode,
        });
        Assert.True(click.Ok, click.Message);
        window.AssertClicks("Tiny", 1);
        Assert.NotNull(click.Element);
        Assert.Equal("Tiny", click.Element!.Name);
        Assert.Equal(DesktopToolRuntime.VerificationVerified, click.Verification!.Status);
    }

    [Fact]
    public async Task ClickByElement_AutomationIdResolvesAndClicks()
    {
        var runtime = new DesktopToolRuntime(() => TempHome());
        using var window = new ElementsWindow();

        var found = runtime.FindElement(new DesktopFindElementArgs { WindowId = window.Hwnd, ControlType = "button", Mode = DesktopToolRuntime.IsolatedMode });
        Assert.True(found.Ok, found.Message);
        var save = found.Elements!.First(element => element.Name == "Save");
        Assert.NotEmpty(save.AutomationId); // classic buttons expose their control id as the automation id

        var click = await runtime.ClickAsync(new DesktopClickArgs
        {
            ElementAutomationId = save.AutomationId, WindowId = window.Hwnd, Mode = DesktopToolRuntime.IsolatedMode,
        });
        Assert.True(click.Ok, click.Message);
        window.AssertClicks("Save", 1);
        Assert.Equal("Save", click.Element!.Name);
    }

    [Fact]
    public async Task ClickByElement_WithoutFindElementIsRefused()
    {
        var runtime = new DesktopToolRuntime(() => TempHome());
        using var window = new ElementsWindow();

        var click = await runtime.ClickAsync(new DesktopClickArgs
        {
            ElementIndex = 0, WindowId = window.Hwnd, Mode = DesktopToolRuntime.IsolatedMode,
        });
        Assert.False(click.Ok);
        Assert.Equal("desktop_element_context_missing", click.Code);
        window.AssertClicks("Save", 0);

        // out of range against a real cached page
        Assert.True(runtime.FindElement(new DesktopFindElementArgs { WindowId = window.Hwnd, ControlType = "button", Mode = DesktopToolRuntime.IsolatedMode }).Ok);
        var outOfRange = await runtime.ClickAsync(new DesktopClickArgs
        {
            ElementIndex = 99, WindowId = window.Hwnd, Mode = DesktopToolRuntime.IsolatedMode,
        });
        Assert.False(outOfRange.Ok);
        Assert.Equal("desktop_element_not_found", outOfRange.Code);
        window.AssertClicks("Save", 0);
    }

    /// <summary>Big trees page by offset: consecutive pages are disjoint, the total is
    /// stable across pages, and an offset past the end is an EMPTY page, not an error
    /// (the model must learn "no more elements", not "something broke").</summary>
    [Fact]
    public async Task FindElement_OffsetPagesTheMatchList()
    {
        var runtime = new DesktopToolRuntime(() => TempHome());
        using var window = new ElementsWindow();

        var first = runtime.FindElement(new DesktopFindElementArgs { WindowId = window.Hwnd, Limit = 10, Mode = DesktopToolRuntime.IsolatedMode });
        Assert.True(first.Ok, first.Message);
        Assert.Equal(10, first.Count);
        Assert.True(first.Total > 20, $"expected a tree big enough to page, got {first.Total}");
        Assert.Equal(0, first.Offset);
        Assert.Contains("offset=10", first.Message, StringComparison.OrdinalIgnoreCase);

        var second = runtime.FindElement(new DesktopFindElementArgs { WindowId = window.Hwnd, Limit = 10, Offset = 10, Mode = DesktopToolRuntime.IsolatedMode });
        Assert.True(second.Ok, second.Message);
        Assert.Equal(10, second.Count);
        Assert.Equal(10, second.Offset);
        Assert.Equal(first.Total, second.Total);
        var firstKeys = first.Elements!.Select(e => (e.Name, e.Bounds!.X, e.Bounds!.Y)).ToHashSet();
        var secondKeys = second.Elements!.Select(e => (e.Name, e.Bounds!.X, e.Bounds!.Y)).ToHashSet();
        Assert.Empty(firstKeys.Intersect(secondKeys));

        var beyond = runtime.FindElement(new DesktopFindElementArgs { WindowId = window.Hwnd, Limit = 10, Offset = 100000, Mode = DesktopToolRuntime.IsolatedMode });
        Assert.True(beyond.Ok, beyond.Message);
        Assert.Equal(0, beyond.Count);
        Assert.Empty(beyond.Elements!);
    }

    /// <summary>offscreen=false hides the listbox items below the fold: fewer matches,
    /// and none of the returned ones reports IsOffscreen.</summary>
    [Fact]
    public async Task FindElement_OffscreenFalseHidesListItemsBelowTheFold()
    {
        var runtime = new DesktopToolRuntime(() => TempHome());
        using var window = new ElementsWindow();

        var all = runtime.FindElement(new DesktopFindElementArgs { WindowId = window.Hwnd, ControlType = "listitem", Limit = 50, Mode = DesktopToolRuntime.IsolatedMode });
        Assert.True(all.Ok, all.Message);
        Assert.Equal(30, all.Total); // the harness listbox holds exactly 30 items

        var visible = runtime.FindElement(new DesktopFindElementArgs { WindowId = window.Hwnd, ControlType = "listitem", Limit = 50, Offscreen = false, Mode = DesktopToolRuntime.IsolatedMode });
        Assert.True(visible.Ok, visible.Message);
        Assert.True(visible.Total < 30, $"expected the fold to hide items, but all {visible.Total} are visible");
        Assert.All(visible.Elements!, element => Assert.False(element.IsOffscreen));
    }

    /// <summary>The RangeValue positive: set a slider through the element path and read
    /// the value back from the control itself - read-back AND host state must both land
    /// on 75 (probe round 12 also verified this cross-process on a WPF slider).</summary>
    [Fact]
    public async Task SetValue_OnSliderSetsTheValueAndReadsItBack()
    {
        var runtime = new DesktopToolRuntime(() => TempHome());
        using var window = new ElementsWindow();

        var found = runtime.FindElement(new DesktopFindElementArgs { WindowId = window.Hwnd, ControlType = "slider", Mode = DesktopToolRuntime.IsolatedMode });
        Assert.True(found.Ok, found.Message);
        var slider = Assert.Single(found.Elements!);
        Assert.Equal(30, window.SliderPosition()); // TBM_SETPOS initial value

        var set = runtime.SetValue(new DesktopSetValueArgs { WindowId = window.Hwnd, ElementIndex = slider.Index, Value = 75, Mode = DesktopToolRuntime.IsolatedMode });
        Assert.True(set.Ok, set.Message);
        Assert.NotNull(set.Value);
        Assert.Equal(75, set.Value!.Value, 1);
        Assert.Equal("Slider", set.Element!.ControlType);
        window.AssertSliderPosition(75); // the control's own state, read directly
    }

    /// <summary>Not range-capable is a STRUCTURED refusal: a button must never be
    /// 'clicked' by a set_value, and the error says where value setting belongs.</summary>
    [Fact]
    public async Task SetValue_OnButtonIsRefusedAsNotRangeCapable()
    {
        var runtime = new DesktopToolRuntime(() => TempHome());
        using var window = new ElementsWindow();

        var found = runtime.FindElement(new DesktopFindElementArgs { WindowId = window.Hwnd, Name = "Save", Mode = DesktopToolRuntime.IsolatedMode });
        Assert.True(found.Ok, found.Message);
        var save = Assert.Single(found.Elements!);

        var set = runtime.SetValue(new DesktopSetValueArgs { WindowId = window.Hwnd, ElementIndex = save.Index, Value = 50, Mode = DesktopToolRuntime.IsolatedMode });
        Assert.False(set.Ok);
        Assert.Equal("desktop_element_not_range_capable", set.Code);
        window.AssertClicks("Save", 0); // the button was NOT clicked
    }

    // ── state-verified pattern clicks (round 15: the lying-Invoke lesson) ──────

    /// <summary>
    /// The verdict core of the state-verified click chain, pinned as a pure function.
    /// Measured motivation: on a Chromium select popup, invoking the 上海 item while the
    /// popup was still realizing threw nothing and changed nothing - the form submitted
    /// city=beijing although the click had reported ok=true.
    /// </summary>
    [Fact]
    public void ClickTook_RequiresTheReadableStateToAgree()
    {
        // selection-capable: the element must report itself SELECTED after the click
        Assert.False(DesktopAutomation.ClickTook(hasSelectionState: true, selectedAfter: false, hasToggleState: false, toggleBefore: null, toggleAfter: null));
        Assert.True(DesktopAutomation.ClickTook(hasSelectionState: true, selectedAfter: true, hasToggleState: false, toggleBefore: null, toggleAfter: null));
        // toggle-capable: the state must have MOVED between the two reads; an unchanged
        // or unreadable post-state is the silent no-op this exists to catch
        Assert.False(DesktopAutomation.ClickTook(hasSelectionState: false, selectedAfter: false, hasToggleState: true, toggleBefore: 0, toggleAfter: 0));
        Assert.True(DesktopAutomation.ClickTook(hasSelectionState: false, selectedAfter: false, hasToggleState: true, toggleBefore: 0, toggleAfter: 1));
        Assert.False(DesktopAutomation.ClickTook(hasSelectionState: false, selectedAfter: false, hasToggleState: true, toggleBefore: 0, toggleAfter: null));
        // no readable state: the no-exception call remains the best evidence there is
        Assert.True(DesktopAutomation.ClickTook(hasSelectionState: false, selectedAfter: false, hasToggleState: false, toggleBefore: null, toggleAfter: null));
    }

    /// <summary>A listbox item click through the pattern chain must verifiably select the
    /// item: delivery uia-select AND the control's own selection state (LB_GETCURSEL from
    /// the host) moved. This is the same verification ClickByPattern now applies to every
    /// selection-capable element, exercised on a real window.</summary>
    [Fact]
    public async Task ClickByElement_ListboxSelectionIsStateVerified()
    {
        var runtime = new DesktopToolRuntime(() => TempHome());
        using var window = new ElementsWindow();

        var found = runtime.FindElement(new DesktopFindElementArgs { WindowId = window.Hwnd, Name = "Second", ControlType = "ListItem", Mode = DesktopToolRuntime.IsolatedMode });
        Assert.True(found.Ok, found.Message);
        var second = Assert.Single(found.Elements!);

        var click = await runtime.ClickAsync(new DesktopClickArgs { ElementIndex = second.Index, WindowId = window.Hwnd, Mode = DesktopToolRuntime.IsolatedMode });
        Assert.True(click.Ok, click.Message);
        Assert.Equal("uia-select", click.Delivery);
        window.AssertListSelection(1); // 'Second' is the 0-based index 1 - the control's own state
    }

    /// <summary>
    /// Typing into a checkbox through the element path is a MEASURED silent no-op (probe
    /// round 15: delivery=uia-value, ok=true, the box stayed unchecked and the form
    /// submitted without the field) - it must be refused with the actionable path instead.
    /// </summary>
    [Fact]
    public async Task TypeIntoCheckboxByElementIsRefusedNotSilentlyAccepted()
    {
        var runtime = new DesktopToolRuntime(() => TempHome());
        using var window = new ElementsWindow();

        var found = runtime.FindElement(new DesktopFindElementArgs { WindowId = window.Hwnd, ControlType = "CheckBox", Mode = DesktopToolRuntime.IsolatedMode });
        Assert.True(found.Ok, found.Message);
        Assert.True((found.Total ?? 0) >= 1, "the harness checkbox must be visible to UIA as a CheckBox");
        Assert.Equal(0, window.CheckState());

        var typed = await runtime.TypeAsync(new DesktopTypeArgs { Text = " ", ElementIndex = found.Elements![0].Index, WindowId = window.Hwnd, Mode = DesktopToolRuntime.IsolatedMode });
        Assert.False(typed.Ok);
        Assert.Equal("desktop_element_not_typeable", typed.Code);
        Assert.Contains("desktop_click", typed.Message, StringComparison.Ordinal);
        Assert.Equal(0, window.CheckState()); // nothing toggled - the refusal happened before any input
    }

    [Fact]
    public async Task SetValue_WithoutFindElementContextIsRefused()
    {
        var runtime = new DesktopToolRuntime(() => TempHome());
        using var window = new ElementsWindow();

        var set = runtime.SetValue(new DesktopSetValueArgs { WindowId = window.Hwnd, ElementIndex = 0, Value = 50, Mode = DesktopToolRuntime.IsolatedMode });
        Assert.False(set.Ok);
        Assert.Equal("desktop_element_context_missing", set.Code);
    }

    /// <summary>Round 13: emoji (surrogate pairs) into a classic Edit control must route
    /// through EM_REPLACESEL (WM_CHAR mangles lone surrogates into '?') and land
    /// EXACTLY - every UTF-16 code unit.</summary>
    [Fact]
    public async Task Type_EmojiIntoEditGoesThroughReplaceSelAndLandsExactly()
    {
        var runtime = new DesktopToolRuntime(() => TempHome());
        using var window = new ElementsWindow();
        const string text = "中文①：你好！🎂 café，对吗？";

        var typed = await runtime.TypeAsync(new DesktopTypeArgs { Text = text, WindowId = window.Edit, Mode = DesktopToolRuntime.IsolatedMode });
        Assert.True(typed.Ok, typed.Message);
        Assert.Equal("replace-sel", typed.Delivery);

        var landed = false;
        for (var attempt = 0; attempt < 30 && !landed; attempt++)
        {
            landed = DesktopWin32.WindowTitle(window.Edit) == text;
            if (!landed) Thread.Sleep(50);
        }
        Assert.Equal(text, DesktopWin32.WindowTitle(window.Edit));
    }

    /// <summary>Text without surrogate pairs keeps the plain WM_CHAR delivery (round 13:
    /// CJK + full-width punctuation + circled digits all survive WM_CHAR into an Edit).</summary>
    [Fact]
    public async Task Type_PlainChineseIntoEditStaysOnWmChar()
    {
        var runtime = new DesktopToolRuntime(() => TempHome());
        using var window = new ElementsWindow();
        const string text = "你好，世界。测试①：全角标点";

        var typed = await runtime.TypeAsync(new DesktopTypeArgs { Text = text, WindowId = window.Edit, Mode = DesktopToolRuntime.IsolatedMode });
        Assert.True(typed.Ok, typed.Message);
        Assert.Equal("wm-char", typed.Delivery);

        var landed = false;
        for (var attempt = 0; attempt < 30 && !landed; attempt++)
        {
            landed = DesktopWin32.WindowTitle(window.Edit) == text;
            if (!landed) Thread.Sleep(50);
        }
        Assert.Equal(text, DesktopWin32.WindowTitle(window.Edit));
    }

    /// <summary>The occlusion guard: a topmost overlay over the target's button must refuse
    /// the click with desktop_point_occluded, and the target must receive NOTHING (the
    /// round-9 lesson: a full-screen dim layer silently swallowed clicks).</summary>
    [Fact]
    public async Task Click_IsRefusedWhenAnOverlayCoversTheTarget()
    {
        var runtime = new DesktopToolRuntime(() => TempHome());
        using var window = new ElementsWindow();
        using var overlay = new OverlayWindow(window);

        var click = await runtime.ClickAsync(new DesktopClickArgs
        {
            X = 80, Y = 40, WindowId = window.Hwnd, Mode = DesktopToolRuntime.IsolatedMode,
        });
        Assert.False(click.Ok);
        Assert.Equal("desktop_point_occluded", click.Code);
        Assert.Contains("refusing to click through", click.Message, StringComparison.Ordinal);
        window.AssertClicks("Save", 0);
    }

    /// <summary>Typing by element goes through the Value pattern and is audited as
    /// uia-value; a native edit falls back to posting WM_CHAR into its own window.</summary>
    [Fact]
    public async Task TypeByElement_SetsTheValueAndReportsTheDelivery()
    {
        var runtime = new DesktopToolRuntime(() => TempHome());
        using var window = new ElementsWindow();
        Assert.True(runtime.FindElement(new DesktopFindElementArgs { WindowId = window.Hwnd, Mode = DesktopToolRuntime.IsolatedMode }).Ok);
        var edit = runtime.FindElement(new DesktopFindElementArgs { WindowId = window.Hwnd, ClassName = "Edit", Mode = DesktopToolRuntime.IsolatedMode });
        Assert.True(edit.Ok, edit.Message);
        Assert.NotEmpty(edit.Elements!);

        var typed = await runtime.TypeAsync(new DesktopTypeArgs
        {
            Text = "element-typed", ElementIndex = edit.Elements![0].Index, WindowId = window.Hwnd, Mode = DesktopToolRuntime.IsolatedMode,
        });
        Assert.True(typed.Ok, typed.Message);
        Assert.NotNull(typed.Delivery);
        Assert.Equal("element-typed".Length, typed.Chars);
        // the text really landed in the edit control (app state, not the tool's word)
        var landed = false;
        for (var attempt = 0; attempt < 20 && !landed; attempt++)
        {
            landed = DesktopWin32.WindowTitle(window.Edit) == "element-typed";
            if (!landed) Thread.Sleep(50);
        }
        Assert.True(landed, $"edit content was '{DesktopWin32.WindowTitle(window.Edit)}'");
    }

    /// <summary>An element outside the visible area whose container cannot scroll it in is
    /// refused with desktop_element_offscreen - an explicit refusal, never a silent click
    /// into whatever happens to be at the stale coordinates. Probe-measured: a Win32
    /// BUTTON clipped by its parent does NOT report IsOffscreen (still clickable by
    /// native-post), but a LISTBOX item below the fold DOES - classic listbox items
    /// expose no ScrollItem, so the refusal fires.</summary>
    [Fact]
    public async Task ClickByElement_OffscreenWithoutScrollIsRefusedExplicitly()
    {
        var runtime = new DesktopToolRuntime(() => TempHome());
        using var window = new ElementsWindow();
        var found = runtime.FindElement(new DesktopFindElementArgs { WindowId = window.Hwnd, Name = "Item30", Mode = DesktopToolRuntime.IsolatedMode });
        Assert.True(found.Ok, found.Message);
        var item30 = found.Elements!.Single();
        Assert.True(item30.IsOffscreen, "Item30 sits below the listbox fold - if it is not offscreen this test's premise broke");

        var click = await runtime.ClickAsync(new DesktopClickArgs
        {
            ElementIndex = item30.Index, WindowId = window.Hwnd, Mode = DesktopToolRuntime.IsolatedMode,
        });
        Assert.False(click.Ok);
        Assert.Equal("desktop_element_offscreen", click.Code);
        Assert.Contains("visible area", click.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An isolated window with three BUTTON children (Save/Cancel + a 16x16 Tiny) whose
    /// click counts are rendered into their labels, an edit field, and a button placed far
    /// below the visible client area (the offscreen-refusal probe).
    /// </summary>
    private sealed class ElementsWindow : IsolatedWindow
    {
        private static readonly ConcurrentDictionary<string, int> Clicks = new();
        private static readonly ConcurrentDictionary<nint, string> Names = new();

        public nint Edit { get; private set; }
        public nint Slider { get; private set; }
        public nint Check { get; private set; }
        public nint List { get; private set; }

        public ElementsWindow()
            : base(400, 420, 60, "")
        {
        }

        /// <summary>The slider's position read straight from the control (TBM_GETPOS) -
        /// the host-state evidence for desktop_set_value tests.</summary>
        public int SliderPosition() => (int)SendMessage(Slider, 0x0400 /* TBM_GETPOS */, 0, 0);

        /// <summary>The checkbox's check state straight from the control (BM_GETCHECK) -
        /// 0 unchecked, 1 checked. The host-state evidence for the typing-refusal test.</summary>
        public int CheckState() => (int)SendMessage(Check, 0x00F0 /* BM_GETCHECK */, 0, 0);

        /// <summary>The listbox's selected index straight from the control (LB_GETCURSEL) -
        /// the host-state evidence that a pattern click really selected the item.</summary>
        public int ListSelection() => (int)SendMessage(List, 0x0188 /* LB_GETCURSEL */, 0, 0);

        /// <summary>Waits until the listbox reports the expected selection (the UIA
        /// Select settles asynchronously through the provider).</summary>
        public void AssertListSelection(int expected)
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (ListSelection() == expected) return;
                Thread.Sleep(50);
            }
            Assert.Equal(expected, ListSelection());
        }

        /// <summary>Waits until the slider reports the expected position (the UIA
        /// SetValue settles asynchronously through the provider).</summary>
        public void AssertSliderPosition(int expected)
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (SliderPosition() == expected) return;
                Thread.Sleep(50);
            }
            Assert.Equal(expected, SliderPosition());
        }

        protected override nint CreateChildren(nint hwnd)
        {
            Create(hwnd, "Save", 101, 20, 20, 120, 40);
            Create(hwnd, "Cancel", 102, 160, 20, 120, 40);
            Create(hwnd, "Tiny", 103, 20, 80, 16, 16);
            Create(hwnd, "Clipped", 106, 20, 460, 120, 32);
            Edit = CreateWindowEx(0x00000200u, "EDIT", "", WS_CHILD | WS_VISIBLE | 0x80u /* ES_AUTOHSCROLL */, 160, 80, 150, 24, hwnd, (nint)105, GetModuleHandle(null), 0);
            // 30 items in a 70px box: items below the fold report IsOffscreen (the probe
            // measured that clipped BUTTONS do not, but listbox ITEMS do)
            var list = CreateWindowEx(0x00000200u, "LISTBOX", "", WS_CHILD | WS_VISIBLE | 0x1u /* LBS_NOTIFY */, 160, 115, 150, 70, hwnd, (nint)107, GetModuleHandle(null), 0);
            List = list;
            foreach (var name in new[] { "First", "Second", "Third" }.Concat(Enumerable.Range(4, 27).Select(n => $"Item{n:00}")))
                SendMessageString(list, 0x0180 /* LB_ADDSTRING */, 0, new StringBuilder(name));
            // a trackbar (0..100, position 30): the RangeValue positive for desktop_set_value
            Slider = CreateWindowEx(0, "msctls_trackbar32", "", WS_CHILD | WS_VISIBLE, 160, 195, 150, 28, hwnd, (nint)108, GetModuleHandle(null), 0);
            SendMessage(Slider, 0x0401 /* TBM_SETRANGE */, 0, (nint)((100 << 16) | 0));
            SendMessage(Slider, 0x0405 /* TBM_SETPOS */, 0, 30);
            // an auto-checkbox: the refusal target for desktop_type's element path
            Check = CreateWindowEx(0, "BUTTON", "Agree to terms", WS_CHILD | WS_VISIBLE | 0x2u /* BS_AUTOCHECKBOX */, 20, 240, 150, 24, hwnd, (nint)109, GetModuleHandle(null), 0);
            return 0;

            void Create(nint parent, string name, int id, int x, int y, int w, int h)
            {
                var button = CreateWindowEx(0, "BUTTON", name, WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON, x, y, w, h, parent, (nint)id, GetModuleHandle(null), 0);
                Clicks[name] = 0;
                Names[button] = name;
            }
        }

        public void AssertClicks(string name, int expected)
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (Clicks.GetValueOrDefault(name) == expected) return;
                Thread.Sleep(50);
            }
            Assert.Equal(expected, Clicks.GetValueOrDefault(name));
        }

        protected override void DisposeWindow(nint hwnd)
        {
            Clicks.Clear();
            Names.Clear();
        }

        protected override nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
        {
            if (msg == WM_COMMAND && ((wParam >> 16) & 0xFFFF) == 0)
            {
                if (Names.TryGetValue(lParam, out var name))
                {
                    var count = Clicks.AddOrUpdate(name, 1, (_, value) => value + 1);
                    SetWindowText(lParam, $"{name} ({count})");
                }
                return 0;
            }
            return base.WndProc(hWnd, msg, wParam, lParam);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
        private static extern nint SendMessageString(nint hWnd, uint msg, nint wParam, StringBuilder lParam);
        [DllImport("user32.dll")] private static extern nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);
    }

    /// <summary>A topmost window placed exactly over the target's Save button area.</summary>
    private sealed class OverlayWindow : IsolatedWindow
    {
        public OverlayWindow(ElementsWindow target)
            : base(400, 420, 60, "", topmost: true)
        {
            // same position as the target: created later + topmost, so it sits on top
        }

        protected override nint CreateChildren(nint hwnd) => 0;

        protected override void DisposeWindow(nint hwnd)
        {
        }
    }

    // ── circuit breaker: consecutive failed verifications stop coordinate clicking ──

    /// <summary>Three consecutive failed verifications trip the breaker: the third announces
    /// the suspension, and a fourth compensated click - even one aimed correctly at the
    /// button - is refused before any input is delivered.</summary>
    [Fact]
    public async Task Compensation_ConsecutiveFailuresSuspendCoordinateClicking()
    {
        var home = TempHome();
        EnableCompensation(home);
        WriteProfiles(home, CoveringProfile);
        var runtime = new DesktopToolRuntime(() => home);
        using var window = new ButtonWindow(width: 320, height: 500);
        var shot = window.Screenshot(runtime);
        Assert.True(shot.Ok, shot.Message);
        var (deadX, deadY) = window.ModelCoordsForClientPoint(160, 300, scale: 2.0, shot);
        var (goodX, goodY) = window.ModelCoordsForClientPoint(120, 50, scale: 2.0, shot);

        for (var failure = 1; failure <= 3; failure++)
        {
            var click = await runtime.ClickAsync(new DesktopClickArgs
            {
                X = deadX, Y = deadY, WindowId = window.Hwnd, Mode = DesktopToolRuntime.IsolatedMode,
            });
            Assert.True(click.Ok, click.Message);
            Assert.Equal(DesktopToolRuntime.VerificationFailed, click.Verification!.Status);
            if (failure < 3)
                Assert.DoesNotContain("SUSPENDED", click.Message, StringComparison.Ordinal);
            else
                Assert.Contains("SUSPENDED", click.Message, StringComparison.Ordinal);
        }
        window.AssertClicks(0);

        // even a correctly-aimed compensated click is now refused before delivery
        var refused = await runtime.ClickAsync(new DesktopClickArgs
        {
            X = goodX, Y = goodY, WindowId = window.Hwnd, Mode = DesktopToolRuntime.IsolatedMode,
        });
        Assert.False(refused.Ok);
        Assert.Equal(DesktopToolRuntime.CompensationSuspendedCode, refused.Code);
        Assert.Contains("desktop_read_text", refused.Message, StringComparison.Ordinal);
        Assert.Null(refused.Compensation);
        window.AssertClicks(0);
    }

    [Fact]
    public async Task Compensation_VerifiedClickResetsTheFailureStreak()
    {
        var home = TempHome();
        EnableCompensation(home);
        WriteProfiles(home, CoveringProfile);
        var runtime = new DesktopToolRuntime(() => home);
        using var window = new ButtonWindow(width: 320, height: 500);
        var shot = window.Screenshot(runtime);
        Assert.True(shot.Ok, shot.Message);
        var (deadX, deadY) = window.ModelCoordsForClientPoint(160, 300, scale: 2.0, shot);
        var (goodX, goodY) = window.ModelCoordsForClientPoint(120, 50, scale: 2.0, shot);

        async Task<DesktopToolResult> ClickAt(int x, int y) => await runtime.ClickAsync(new DesktopClickArgs
        {
            X = x, Y = y, WindowId = window.Hwnd, Mode = DesktopToolRuntime.IsolatedMode,
        });

        // two failures, then a verified click resets the streak
        Assert.Equal(DesktopToolRuntime.VerificationFailed, (await ClickAt(deadX, deadY)).Verification!.Status);
        Assert.Equal(DesktopToolRuntime.VerificationFailed, (await ClickAt(deadX, deadY)).Verification!.Status);
        var verified = await ClickAt(goodX, goodY);
        Assert.Equal(DesktopToolRuntime.VerificationVerified, verified.Verification!.Status);
        window.AssertClicks(1);

        // two more failures after the reset: still below the limit, the click still executes
        Assert.Equal(DesktopToolRuntime.VerificationFailed, (await ClickAt(deadX, deadY)).Verification!.Status);
        var fifth = await ClickAt(deadX, deadY);
        Assert.Equal(DesktopToolRuntime.VerificationFailed, fifth.Verification!.Status);
        Assert.DoesNotContain("SUSPENDED", fifth.Message, StringComparison.Ordinal);
    }

    /// <summary>`unverified` means the target had no readable text - that is not evidence the
    /// scale is wrong, so it must not count toward the breaker.</summary>
    [Fact]
    public async Task Compensation_UnverifiedClicksDoNotTripTheBreaker()
    {
        var home = TempHome();
        EnableCompensation(home);
        WriteProfiles(home, CoveringProfile);
        var runtime = new DesktopToolRuntime(() => home);
        using var plain = new PlainWindow();
        // separate screen areas: overlapping test windows would steal each other's hit-tests
        using var window = new ButtonWindow(width: 320, height: 500, x: 500);

        // three unverified clicks on a textless window
        var plainShot = plain.Screenshot(runtime);
        Assert.True(plainShot.Ok, plainShot.Message);
        var (plainX, plainY) = plain.ModelCoordsForClientPoint(150, 90, scale: 2.0, plainShot);
        for (var i = 0; i < 3; i++)
        {
            var click = await runtime.ClickAsync(new DesktopClickArgs
            {
                X = plainX, Y = plainY, WindowId = plain.Hwnd, Mode = DesktopToolRuntime.IsolatedMode,
            });
            Assert.Equal(DesktopToolRuntime.VerificationUnverified, click.Verification!.Status);
        }

        // a subsequent compensated click on a text-bearing window still executes
        var shot = window.Screenshot(runtime);
        Assert.True(shot.Ok, shot.Message);
        var (deadX, deadY) = window.ModelCoordsForClientPoint(160, 300, scale: 2.0, shot);
        var clickAfter = await runtime.ClickAsync(new DesktopClickArgs
        {
            X = deadX, Y = deadY, WindowId = window.Hwnd, Mode = DesktopToolRuntime.IsolatedMode,
        });
        Assert.True(clickAfter.Ok, clickAfter.Message);
        Assert.Equal(DesktopToolRuntime.VerificationFailed, clickAfter.Verification!.Status);
        Assert.DoesNotContain("SUSPENDED", clickAfter.Message, StringComparison.Ordinal);
        window.AssertClicks(0);
    }

    // ── profile store unit behavior ────────────────────────────────────────────

    [Fact]
    public void ProfileStore_BoundsAreInclusiveAndFirstRowWins()
    {
        var home = TempHome();
        WriteProfiles(home,
            """{"schema_version": 1, "profiles": [{"model": "m", "protocol": "p", "aspect_min": 1.0, "aspect_max": 2.0, "short_edge_min": 400, "short_edge_max": 800, "scale_x": 1.5, "scale_y": 1.5, "samples": 5, "hit_rate": 0.9, "tested": "2026-11-05"}, {"model": "m", "protocol": "p", "aspect_min": 0.5, "aspect_max": 3.0, "short_edge_min": 100, "short_edge_max": 1600, "scale_x": 1.754, "scale_y": 1.754, "samples": 30, "hit_rate": 1.0, "tested": "2026-11-05"}]}""");
        var store = CoordinateProfileStore.Load(home);

        // exact upper bound matches, and the first matching row in file order wins
        var hit = store.Find("M", "P", 2.0, 400);
        Assert.NotNull(hit);
        Assert.Equal(1.5, hit!.ScaleX);

        // the first row does not cover aspect 2.5, but the second (broader) row does
        var broad = store.Find("m", "p", 2.5, 400);
        Assert.NotNull(broad);
        Assert.Equal(1.754, broad!.ScaleX);

        // outside every row
        Assert.Null(store.Find("m", "p", 3.5, 400));
    }

    [Fact]
    public void ProfileStore_DescribeCoverageNamesWhatIsMeasured()
    {
        var home = TempHome();
        WriteProfiles(home, CoveringProfile);
        var store = CoordinateProfileStore.Load(home);

        var unknown = store.DescribeCoverage("other-model", "test-protocol");
        Assert.Contains("No profile row exists", unknown, StringComparison.Ordinal);
        Assert.Contains("test-model/test-protocol", unknown, StringComparison.Ordinal);

        var known = store.DescribeCoverage("test-model", "test-protocol");
        Assert.Contains("aspect 0.5–3.6", known, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileStore_OutOfRangeRowFailsClosed()
    {
        var home = TempHome();
        WriteProfiles(home,
            """{"schema_version": 1, "profiles": [{"model": "m", "protocol": "p", "aspect_min": 2.0, "aspect_max": 1.0, "short_edge_min": 100, "short_edge_max": 1600, "scale_x": 2.0, "scale_y": 2.0, "samples": 5, "hit_rate": 1.0}]}""");
        Assert.Throws<DesktopToolRuntime.DesktopConfigurationException>(() => CoordinateProfileStore.Load(home));
    }

    // ── real windows on the isolated desktop, the clickloop harness pattern ────

    /// <summary>
    /// A window with a BUTTON child (client rect 20,20–220,80) whose click count is rendered
    /// into its label ("Push (n)") - the same shape the clickloop harness uses, so `verified`
    /// verdicts here mean the same thing they mean in the real-machine measurements.
    /// </summary>
    private sealed class ButtonWindow : IsolatedWindow
    {
        private static readonly ConcurrentDictionary<nint, int> Clicks = new();
        private static readonly ConcurrentDictionary<nint, nint> Buttons = new();

        public ButtonWindow(int width = 320, int height = 200, int x = 60)
            : base(width, height, x, "")
        {
            // empty title on purpose: this window pattern's lpWindowName marshals to
            // uninitialized memory (probe-measured: the title reads back as different garbage
            // on every WM_GETTEXT), and a garbage root text would make the semantic
            // verification compare noise. The button label is the only text that matters.
        }

        protected override nint CreateChildren(nint hwnd)
        {
            var button = CreateWindowEx(0, "BUTTON", "Push", WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
                20, 20, 200, 60, hwnd, 0, GetModuleHandle(null), 0);
            Clicks[hwnd] = 0;
            Buttons[hwnd] = button;
            return button;
        }

        public void AssertClicks(int expected)
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (Clicks.GetValueOrDefault(Hwnd) == expected) return;
                Thread.Sleep(50);
            }
            Assert.Equal(expected, Clicks.GetValueOrDefault(Hwnd));
        }

        protected override void DisposeWindow(nint hwnd)
        {
            Clicks.TryRemove(hwnd, out _);
            Buttons.TryRemove(hwnd, out _);
        }

        protected override nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
        {
            if (msg == WM_COMMAND && ((wParam >> 16) & 0xFFFF) == 0 /* BN_CLICKED */)
            {
                var count = Clicks.AddOrUpdate(hWnd, 1, (_, value) => value + 1);
                if (Buttons.TryGetValue(hWnd, out var button))
                    SetWindowText(button, $"Push ({count})");
                return 0;
            }
            return base.WndProc(hWnd, msg, wParam, lParam);
        }
    }

    /// <summary>A window with no readable text at all: empty title, no children.</summary>
    private sealed class PlainWindow : IsolatedWindow
    {
        public PlainWindow()
            : base(320, 200, 60, "")
        {
        }

        protected override nint CreateChildren(nint hwnd) => 0;

        protected override void DisposeWindow(nint hwnd)
        {
        }
    }

    /// <summary>
    /// A window whose WM_GETTEXT answer differs on every read (an unstable text source, the
    /// measured failure mode behind false `verified` verdicts: the round-8 harness window's
    /// title marshalled into uninitialized memory read back as different garbage each time,
    /// so four real misses came back "verified"). The verification must not treat per-read
    /// churn as a click effect.
    /// </summary>
    private sealed class ChurnWindow : IsolatedWindow
    {
        private static int _reads;

        public ChurnWindow()
            : base(320, 200, 500, "")
        {
        }

        protected override nint CreateChildren(nint hwnd) => 0;

        protected override void DisposeWindow(nint hwnd)
        {
        }

        protected override nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
        {
            const uint WM_GETTEXT = 0x000D;
            const uint WM_GETTEXTLENGTH = 0x000E;
            if (hWnd == Hwnd && msg == WM_GETTEXTLENGTH)
                return 16;
            if (hWnd == Hwnd && msg == WM_GETTEXT)
            {
                var text = $"churn-{Interlocked.Increment(ref _reads)}";
                var capacity = Math.Min(text.Length, (int)wParam - 1);
                for (var i = 0; i < capacity; i++)
                    Marshal.WriteInt16(lParam, i * 2, text[i]);
                Marshal.WriteInt16(lParam, capacity * 2, 0);
                return capacity;
            }
            return base.WndProc(hWnd, msg, wParam, lParam);
        }
    }

    /// <summary>
    /// A pumping top-level window on the TinadecIsolated desktop (WS_EX_TOOLWINDOW, like the
    /// clickloop harness): the user's desktop stays untouched, and WindowFromPoint during a
    /// silent/isolated click can only ever resolve inside this window tree. Cross-thread work
    /// (close, label reset) goes through the harness's action-queue pattern.
    /// </summary>
    private abstract class IsolatedWindow : IDisposable
    {
        protected const uint WS_OVERLAPPEDWINDOW = 0x00CF0000u;
        protected const uint WS_VISIBLE = 0x10000000u;
        protected const uint WS_CHILD = 0x40000000u;
        protected const uint BS_PUSHBUTTON = 0x1u;
        protected const uint WM_CLOSE = 0x0010u;
        protected const uint WM_DESTROY = 0x0002u;
        protected const uint WM_COMMAND = 0x0111u;
        private const uint WS_EX_TOOLWINDOW = 0x00000080u;
        private const uint WS_EX_TOPMOST = 0x00000008u;
        private const uint ActionMessage = 0x8000;

        private static readonly List<WndProcDelegate> KeepAlive = [];
        private readonly ConcurrentQueue<(Func<object?> Action, TaskCompletionSource<object?> Tcs)> _actions = new();
        private readonly Thread _thread;
        private readonly ManualResetEvent _ready = new(false);
        private readonly int _width;
        private readonly int _height;
        private readonly int _x;
        private readonly string _title;
        private readonly bool _topmost;
        private uint _threadId;

        public nint Hwnd { get; private set; }

        protected IsolatedWindow(int width, int height, int x, string title, bool topmost = false)
        {
            _width = width;
            _height = height;
            _x = x;
            _title = title;
            _topmost = topmost;
            _thread = new Thread(Run) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
            Assert.True(_ready.WaitOne(TimeSpan.FromSeconds(5)), "isolated test window thread did not start");
        }

        public DesktopToolResult Screenshot(DesktopToolRuntime runtime) =>
            runtime.Screenshot(new DesktopScreenshotArgs { WindowId = Hwnd, Mode = DesktopToolRuntime.IsolatedMode });

        /// <summary>
        /// Maps a client point of this window into the model's reported space: client →
        /// image (the capture includes the non-client frame) → ctx copy (÷ full/ctx) →
        /// reported (÷ profile scale). The forward direction of exactly the math the
        /// compensation runs backwards, computed with the same Win32 calls it uses.
        /// </summary>
        public (int X, int Y) ModelCoordsForClientPoint(int clientX, int clientY, double scale, DesktopToolResult shot)
        {
            using var dpi = DesktopWin32.PerMonitorDpiScope.Enter();
            var (windowX, windowY, _, _) = DesktopWin32.WindowBounds(Hwnd);
            var screen = DesktopWin32.ClientToScreen(Hwnd, clientX, clientY);
            var imageX = screen.X - windowX;
            var imageY = screen.Y - windowY;
            var ctxX = imageX * (shot.Image!.Width!.Value / (double)shot.Width!.Value);
            var ctxY = imageY * (shot.Image!.Height!.Value / (double)shot.Height!.Value);
            return ((int)Math.Round(ctxX / scale), (int)Math.Round(ctxY / scale));
        }

        private void Run()
        {
            var desk = DesktopWin32.OpenOrCreateIsolatedDesktop();
            if (desk == 0 || !SetThreadDesktop(desk)) throw new InvalidOperationException($"SetThreadDesktop failed err={Marshal.GetLastWin32Error()}");
            var className = "TinadecCompTest_" + Guid.NewGuid().ToString("N")[..8];
            var proc = new WndProcDelegate(WndProc);
            lock (KeepAlive) KeepAlive.Add(proc);
            var wc = new WNDCLASS { lpfnWndProc = proc, LpszClassName = className, HInstance = GetModuleHandle(null) };
            RegisterClass(ref wc);
            Hwnd = CreateWindowEx(
                (_topmost ? WS_EX_TOOLWINDOW | WS_EX_TOPMOST : WS_EX_TOOLWINDOW), className, _title, WS_OVERLAPPEDWINDOW | WS_VISIBLE,
                _x, 60, _width, _height, 0, 0, GetModuleHandle(null), 0);
            CreateChildren(Hwnd);
            _threadId = GetCurrentThreadId();
            _ready.Set();
            while (GetMessage(out var msg, 0, 0, 0) > 0)
            {
                if (msg.Hwnd == 0 && msg.Message == ActionMessage)
                {
                    while (_actions.TryDequeue(out var item))
                    {
                        try { item.Tcs.SetResult(item.Action()); }
                        catch (Exception ex) { item.Tcs.SetException(ex); }
                    }
                    continue;
                }
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }

        public void Dispose()
        {
            _ = RunOnWindowThread(() => (object?)PostMessage(Hwnd, WM_CLOSE, 0, 0));
            if (!_thread.Join(TimeSpan.FromSeconds(4))) throw new InvalidOperationException("isolated test window thread did not exit");
            DisposeWindow(Hwnd);
            _ready.Dispose();
        }

        protected virtual nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
        {
            if (msg == WM_DESTROY)
            {
                PostQuitMessage(0);
                return 0;
            }
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        protected abstract nint CreateChildren(nint hwnd);
        protected virtual void DisposeWindow(nint hwnd)
        {
        }

        private object? RunOnWindowThread(Func<object?> action)
        {
            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _actions.Enqueue((action, tcs));
            PostThreadMessage(_threadId, ActionMessage, 0, 0);
            return tcs.Task.GetAwaiter().GetResult();
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        protected delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public nint Hwnd;
            public uint Message;
            public nint WParam;
            public nint LParam;
            public uint Time;
            public POINT Point;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASS
        {
            public uint Style;
            public WndProcDelegate lpfnWndProc;
            public int CbClsExtra;
            public int CbWndExtra;
            public nint HInstance;
            public nint HIcon;
            public nint HCursor;
            public nint HbrBackground;
            public string? LpszMenuName;
            public string LpszClassName;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClass(ref WNDCLASS wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] protected static extern nint CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
        [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, nint hWnd, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] private static extern nint DispatchMessage(ref MSG msg);
        [DllImport("user32.dll")] private static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);
        [DllImport("user32.dll")] private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] protected static extern bool SetWindowText(nint hWnd, string text);
        [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(nint desktop);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, nint wParam, nint lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] protected static extern nint GetModuleHandle(string? name);
    }
}
