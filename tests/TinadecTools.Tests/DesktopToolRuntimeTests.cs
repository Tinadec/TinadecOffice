using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TinadecTools.Abstractions;
using TinadecTools.Tools.Desktop;

namespace TinadecTools.Tests;

/// <summary>
/// Desktop computer-use tests. The silent chain runs against a real Win32 window (with an
/// EDIT child) owned by a pumping thread inside the test process: posted messages, capture
/// and WM_GETTEXT all exercise the same code paths a cross-process window would. The fast
/// chain moves the real cursor and is opt-in via TINADEC_DESKTOP_FAST_LIVE=1.
/// </summary>
public sealed class DesktopToolRuntimeTests : IDisposable
{
    private readonly string? _savedModeEnvironment;

    public DesktopToolRuntimeTests()
    {
        // The mode resolution chain reads this variable; pin it off so the tests below
        // observe the config file and defaults deterministically.
        _savedModeEnvironment = Environment.GetEnvironmentVariable("TINADEC_COMPUTER_USE_MODE");
        Environment.SetEnvironmentVariable("TINADEC_COMPUTER_USE_MODE", null);
    }

    public void Dispose() =>
        Environment.SetEnvironmentVariable("TINADEC_COMPUTER_USE_MODE", _savedModeEnvironment);

    private static string TempHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "tinadec-desktop-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, "config"));
        return home;
    }

    // ── mode resolution ────────────────────────────────────────────────────────

    [Fact]
    public void ResolveMode_MissingConfigDefaultsToFast()
    {
        var home = TempHome();
        var runtime = new DesktopToolRuntime(() => home);
        Assert.Equal(DesktopToolRuntime.FastMode, runtime.ResolveMode(null));
    }

    [Fact]
    public void ResolveMode_UsesConfigFileDefault()
    {
        var home = TempHome();
        File.WriteAllText(Path.Combine(home, "config", "computer-use.json"), """{"default_mode": "silent"}""");
        var runtime = new DesktopToolRuntime(() => home);
        Assert.Equal(DesktopToolRuntime.SilentMode, runtime.ResolveMode(null));
    }

    [Fact]
    public void ResolveMode_PerCallArgumentWinsOverConfigFile()
    {
        var home = TempHome();
        File.WriteAllText(Path.Combine(home, "config", "computer-use.json"), """{"default_mode": "silent"}""");
        var runtime = new DesktopToolRuntime(() => home);
        Assert.Equal(DesktopToolRuntime.FastMode, runtime.ResolveMode("fast"));
    }

    [Fact]
    public void ResolveMode_BrokenConfigFailsClosed()
    {
        var home = TempHome();
        File.WriteAllText(Path.Combine(home, "config", "computer-use.json"), "{ not json");
        var runtime = new DesktopToolRuntime(() => home);
        Assert.Throws<DesktopToolRuntime.DesktopConfigurationException>(() => runtime.ResolveMode(null));
    }

    [Fact]
    public void ResolveMode_UnknownConfigValueFailsClosed()
    {
        var home = TempHome();
        File.WriteAllText(Path.Combine(home, "config", "computer-use.json"), """{"default_mode": "invisible"}""");
        var runtime = new DesktopToolRuntime(() => home);
        Assert.Throws<DesktopToolRuntime.DesktopConfigurationException>(() => runtime.ResolveMode(null));
    }

    [Fact]
    public async Task Click_UnknownModeIsRejected()
    {
        var runtime = new DesktopToolRuntime(() => TempHome());
        var result = await runtime.ClickAsync(new DesktopClickArgs { X = 10, Y = 10, Mode = "turbo" });
        Assert.False(result.Ok);
        Assert.Equal("desktop_mode_invalid", result.Code);
    }

    [Fact]
    public async Task Click_SilentModeRequiresWindowId()
    {
        var runtime = new DesktopToolRuntime(() => TempHome());
        var result = await runtime.ClickAsync(new DesktopClickArgs { X = 10, Y = 10, Mode = "silent" });
        Assert.False(result.Ok);
        Assert.Equal("desktop_window_required", result.Code);
    }

    [Fact]
    public async Task Type_FastModeRequiresWindowId()
    {
        var runtime = new DesktopToolRuntime(() => TempHome());
        var result = await runtime.TypeAsync(new DesktopTypeArgs { Text = "hi", Mode = "fast" });
        Assert.False(result.Ok);
        Assert.Equal("desktop_window_required", result.Code);
    }

    // ── silent chain against a real window ─────────────────────────────────────

    [Fact]
    public async Task SilentChain_ScreenshotClickTypeReadAndVerify()
    {
        var home = TempHome();
        var runtime = new DesktopToolRuntime(() => home);
        using var window = new TestWindow();

        // 1. list windows: the test window is discoverable with its metadata
        var list = runtime.ListWindows(new DesktopListWindowsArgs());
        Assert.True(list.Ok);
        var row = Assert.Single(list.Windows!, w => w.WindowId == window.Hwnd);
        Assert.StartsWith("TinadecDeskTest_", row.ClassName, StringComparison.Ordinal);
        Assert.False(row.IsMinimized);

        // 2. window screenshot: PNG lands on disk with the window's physical size, and the small
        //    window doubles as its own context copy (no *-ctx.png duplicate for ≤1568 captures)
        var shot1 = runtime.Screenshot(new DesktopScreenshotArgs { WindowId = window.Hwnd });
        Assert.True(shot1.Ok);
        Assert.True(File.Exists(shot1.Path), $"screenshot file missing: {shot1.Path}");
        var bytes1 = File.ReadAllBytes(shot1.Path!);
        Assert.True(bytes1.Length > 100);
        using (var dpi = DesktopWin32.PerMonitorDpiScope.Enter())
        {
            var (_, _, width, height) = DesktopWin32.WindowBounds(window.Hwnd);
            Assert.Equal(width, shot1.Width);
            Assert.Equal(height, shot1.Height);
        }
        Assert.NotNull(shot1.Image);
        Assert.Equal(shot1.Path, shot1.Image!.Path);
        Assert.Equal("image/png", shot1.Image.MediaType);
        Assert.Equal(shot1.Width, shot1.Image.Width);
        Assert.Equal(shot1.Height, shot1.Image.Height);
        Assert.Equal(shot1.Bytes, shot1.Image.Bytes);

        // 3. silent click into the middle of the edit field (client coordinates)
        var click = await runtime.ClickAsync(new DesktopClickArgs
        {
            X = 150, Y = 30, WindowId = window.Hwnd, Mode = DesktopToolRuntime.SilentMode,
        });
        Assert.True(click.Ok, click.Message);

        // 4. silent typing - WM_CHAR straight into the edit control, no focus change
        var typed = await runtime.TypeAsync(new DesktopTypeArgs
        {
            Text = "Hello from Tinadec", WindowId = window.Hwnd, Mode = DesktopToolRuntime.SilentMode,
        });
        Assert.True(typed.Ok, typed.Message);
        Assert.Equal(window.Edit, typed.WindowId);

        // 5. read the text back (posted messages are pumped asynchronously: poll briefly)
        var found = false;
        for (var attempt = 0; attempt < 40 && !found; attempt++)
        {
            var read = runtime.ReadText(new DesktopReadTextArgs { WindowId = window.Hwnd });
            Assert.True(read.Ok, read.Message);
            var combined = string.Concat(read.Texts!.Select(t => t.Text));
            found = combined.Contains("Hello from Tinadec", StringComparison.Ordinal);
            if (!found) Thread.Sleep(50);
        }
        Assert.True(found, "the typed text never appeared in desktop_read_text");

        // 6. second screenshot differs from the first
        var shot2 = runtime.Screenshot(new DesktopScreenshotArgs { WindowId = window.Hwnd });
        Assert.True(shot2.Ok);
        var bytes2 = File.ReadAllBytes(shot2.Path!);
        Assert.False(bytes1.AsSpan().SequenceEqual(bytes2), "second screenshot is byte-identical; the edit content did not render");
    }

    [Fact]
    public async Task SilentClick_OffWindowPointIsRefused()
    {
        var runtime = new DesktopToolRuntime(() => TempHome());
        using var window = new TestWindow();

        // A client point far outside the window: the occlusion guard sees the point land
        // on something else (another window or bare desktop) and refuses before delivery.
        var result = await runtime.ClickAsync(new DesktopClickArgs
        {
            X = 5000, Y = 5000, WindowId = window.Hwnd, Mode = DesktopToolRuntime.SilentMode,
        });
        Assert.False(result.Ok);
        Assert.Equal("desktop_point_occluded", result.Code);
    }

    [Fact]
    public async Task PressKey_SilentChordIsRejected()
    {
        var runtime = new DesktopToolRuntime(() => TempHome());
        using var window = new TestWindow();

        var chord = await runtime.PressKeyAsync(new DesktopPressKeyArgs
        {
            Keys = "ctrl+a", WindowId = window.Hwnd, Mode = DesktopToolRuntime.SilentMode,
        });
        Assert.False(chord.Ok);
        Assert.Equal("desktop_silent_modifier_unsupported", chord.Code);
    }

    [Fact]
    public async Task Screenshot_WideWindowGetsADownscaledContextCopy()
    {
        var home = TempHome();
        var runtime = new DesktopToolRuntime(() => home);
        using var window = new TestWindow(width: 1700, height: 300);

        var shot = runtime.Screenshot(new DesktopScreenshotArgs { WindowId = window.Hwnd });
        Assert.True(shot.Ok, shot.Message);
        Assert.True(shot.Width > DesktopToolRuntime.MaxContextImageEdge, $"expected a wide window, got {shot.Width}x{shot.Height}");

        // the full capture stays untouched at path; the model-facing copy is a separate *-ctx.png
        Assert.NotNull(shot.Image);
        Assert.NotEqual(shot.Path, shot.Image!.Path);
        Assert.EndsWith("-ctx.png", shot.Image.Path, StringComparison.Ordinal);
        Assert.True(File.Exists(shot.Image.Path), $"context copy missing: {shot.Image.Path}");
        Assert.True(Math.Max(shot.Image.Width!.Value, shot.Image.Height!.Value) <= DesktopToolRuntime.MaxContextImageEdge);
        Assert.Equal("image/png", shot.Image.MediaType);
        Assert.Equal(new FileInfo(shot.Image.Path!).Length, shot.Image.Bytes);
        // the downscaled file is a real PNG, not an empty shell
        Assert.True(shot.Image.Bytes > 100);
        // every image we produce must pass the independent chunk-CRC check, downscaled copies
        // included — the CRC bug was invisible to GDI+ for exactly the files nobody re-checked
        Assert.True(PngChunksAreValid(File.ReadAllBytes(shot.Image.Path!)), "context copy has an invalid PNG structure");
    }

    /// <summary>Independent PNG structure check: signature, chunk walk, per-chunk CRC32, IEND spec constant.</summary>
    private static bool PngChunksAreValid(byte[] bytes)
    {
        if (bytes.Length < 8 || bytes[0] != 0x89 || bytes[1] != 0x50) return false;
        var pos = 8;
        var sawIend = false;
        while (pos + 12 <= bytes.Length && !sawIend)
        {
            var length = (int)((uint)bytes[pos] << 24 | (uint)bytes[pos + 1] << 16 | (uint)bytes[pos + 2] << 8 | bytes[pos + 3]);
            var type = Encoding.ASCII.GetString(bytes, pos + 4, 4);
            var stored = (uint)(bytes[pos + 8 + length] << 24 | bytes[pos + 9 + length] << 16 | (uint)bytes[pos + 10 + length] << 8 | bytes[pos + 11 + length]);
            if (stored != Crc32Over(bytes, pos + 4, 4 + length)) return false;
            if (type == "IEND")
            {
                if (stored != 0xAE426082u) return false;
                sawIend = true;
            }
            pos += 12 + length;
        }
        return sawIend && pos == bytes.Length;
    }

    [Fact]
    public void Downscale_AveragesTheSourceBox()
    {
        // 4x2 image: left half solid blue (BGR: B=255), right half solid red (R=255)
        var bgra = new byte[4 * 2 * 4];
        for (var y = 0; y < 2; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var pixel = (y * 4 + x) * 4;
                if (x < 2) bgra[pixel] = 255; else bgra[pixel + 2] = 255;
            }
        }

        var (small, width, height) = DesktopWin32.Downscale(bgra, 4, 2, 2);
        Assert.Equal(2, width);
        Assert.Equal(1, height);
        // destination pixel 0 averages the solid-blue source half, pixel 1 the solid-red half
        Assert.Equal(255, small[0]);
        Assert.Equal(0, small[1]);
        Assert.Equal(0, small[2]);
        Assert.Equal(255, small[3]);
        Assert.Equal(0, small[4]);
        Assert.Equal(0, small[5]);
        Assert.Equal(255, small[6]);

        // collapsing to 1x1 averages everything: half blue, half red → B=127, R=127
        var (tiny, tinyWidth, tinyHeight) = DesktopWin32.Downscale(bgra, 4, 2, 1);
        Assert.Equal(1, tinyWidth);
        Assert.Equal(1, tinyHeight);
        Assert.Equal(127, tiny[0]);
        Assert.Equal(0, tiny[1]);
        Assert.Equal(127, tiny[2]);
        Assert.Equal(255, tiny[3]);

        // already within budget: returned unchanged
        var (same, sameWidth, sameHeight) = DesktopWin32.Downscale(bgra, 4, 2, 64);
        Assert.True(ReferenceEquals(same, bgra));
        Assert.Equal(4, sameWidth);
        Assert.Equal(2, sameHeight);
    }

    [Fact]
    public async Task Screenshot_PngCarriesValidChunkChecksums()
    {
        var runtime = new DesktopToolRuntime(() => TempHome());
        using var window = new TestWindow();
        var shot = runtime.Screenshot(new DesktopScreenshotArgs { WindowId = window.Hwnd });
        Assert.True(shot.Ok, shot.Message);

        // A model-provider pipeline rejected our PNGs over this: every chunk CRC was computed
        // over the type bytes twice, which GDI+ tolerates and strict decoders do not. The
        // IEND CRC is a spec constant, so this doubles as a regression pin.
        var bytes = File.ReadAllBytes(shot.Path!);
        Assert.Equal(0x89, bytes[0]); // PNG signature
        var pos = 8;
        while (pos + 12 <= bytes.Length)
        {
            var length = (int)((uint)bytes[pos] << 24 | (uint)bytes[pos + 1] << 16 | (uint)bytes[pos + 2] << 8 | bytes[pos + 3]);
            var type = Encoding.ASCII.GetString(bytes, pos + 4, 4);
            var stored = (uint)(bytes[pos + 8 + length] << 24 | bytes[pos + 9 + length] << 16 | (uint)bytes[pos + 10 + length] << 8 | bytes[pos + 11 + length]);
            var computed = Crc32Over(bytes, pos + 4, 4 + length);
            Assert.Equal($"{type}:{stored:X8}", $"{type}:{computed:X8}");
            if (type == "IEND")
            {
                Assert.Equal(0xAE426082u, stored); // spec constant for the empty IEND chunk
                Assert.Equal(pos + 12, bytes.Length);
            }
            pos += 12 + length;
        }
        Assert.Equal(bytes.Length, pos);
    }

    /// <summary>CRC-32 (reflected, poly 0xEDB88320) over a slice — an independent check of what PngEncoder writes.</summary>
    private static uint Crc32Over(byte[] data, int offset, int count)
    {
        uint crc = 0xFFFFFFFF;
        for (var i = 0; i < count; i++)
        {
            crc ^= data[offset + i];
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }

    [Fact]
    public async Task Screenshot_MinimizedWindowIsRefused()
    {
        var runtime = new DesktopToolRuntime(() => TempHome());
        using var window = new TestWindow();
        TestWindow.Minimize(window.Hwnd);
        try
        {
            var result = runtime.Screenshot(new DesktopScreenshotArgs { WindowId = window.Hwnd });
            Assert.False(result.Ok);
            Assert.Equal("desktop_window_minimized", result.Code);
        }
        finally
        {
            TestWindow.Restore(window.Hwnd);
        }
    }

    // ── fast chain (opt-in: it steals focus and moves the real cursor) ─────────

    [Fact]
    public async Task FastChain_Live()
    {
        if (Environment.GetEnvironmentVariable("TINADEC_DESKTOP_FAST_LIVE") != "1")
            return; // set TINADEC_DESKTOP_FAST_LIVE=1 to run: focuses the test window and moves the cursor

        var runtime = new DesktopToolRuntime(() => TempHome());
        using var window = new TestWindow();

        var click = await runtime.ClickAsync(new DesktopClickArgs
        {
            X = 150, Y = 30, WindowId = window.Hwnd, Mode = DesktopToolRuntime.FastMode,
        });
        Assert.True(click.Ok, click.Message);

        var typed = await runtime.TypeAsync(new DesktopTypeArgs
        {
            Text = "fast mode", WindowId = window.Hwnd, Mode = DesktopToolRuntime.FastMode,
        });
        Assert.True(typed.Ok, typed.Message);

        var found = false;
        for (var attempt = 0; attempt < 40 && !found; attempt++)
        {
            var read = runtime.ReadText(new DesktopReadTextArgs { WindowId = window.Hwnd });
            found = string.Concat(read.Texts!.Select(t => t.Text)).Contains("fast mode", StringComparison.Ordinal);
            if (!found) Thread.Sleep(50);
        }
        Assert.True(found, "SendInput text never appeared in the edit control");
    }

    // ── isolated mode (hidden desktop in the same session) ─────────────────────

    [Fact]
    public void ResolveMode_IsolatedComesFromConfigFile()
    {
        var home = TempHome();
        File.WriteAllText(Path.Combine(home, "config", "computer-use.json"), """{"default_mode": "isolated"}""");
        var runtime = new DesktopToolRuntime(() => home);
        Assert.Equal(DesktopToolRuntime.IsolatedMode, runtime.ResolveMode(null));
    }

    [Fact]
    public async Task Click_IsolatedModeRequiresWindowId()
    {
        var runtime = new DesktopToolRuntime(() => TempHome());
        var result = await runtime.ClickAsync(new DesktopClickArgs { X = 10, Y = 10, Mode = DesktopToolRuntime.IsolatedMode });
        Assert.False(result.Ok);
        Assert.Equal("desktop_window_required", result.Code);
    }

    [Fact]
    public async Task Screenshot_IsolatedModeRequiresWindowId()
    {
        var runtime = new DesktopToolRuntime(() => TempHome());
        var result = runtime.Screenshot(new DesktopScreenshotArgs { Mode = DesktopToolRuntime.IsolatedMode });
        Assert.False(result.Ok);
        Assert.Equal("desktop_window_required", result.Code);
    }

    [Fact]
    public async Task PressKey_IsolatedChordIsRejected()
    {
        var runtime = new DesktopToolRuntime(() => TempHome());
        using var window = new TestWindow();
        var result = await runtime.PressKeyAsync(new DesktopPressKeyArgs { Keys = "ctrl+a", WindowId = window.Hwnd, Mode = DesktopToolRuntime.IsolatedMode });
        Assert.False(result.Ok);
        Assert.Equal("desktop_silent_modifier_unsupported", result.Code);
    }

    /// <summary>
    /// End-to-end isolated chain on a real app: launch winver onto the hidden desktop,
    /// find its window there, capture it, read its text, then kill the process. Nothing
    /// ever appears on the user's screen.
    /// </summary>
    [Fact]
    public async Task IsolatedChain_LaunchListScreenshotRead()
    {
        var runtime = new DesktopToolRuntime(() => TempHome());

        var launch = runtime.LaunchApp(new DesktopLaunchArgs { Command = "winver.exe" });
        Assert.True(launch.Ok, launch.Message);
        Assert.NotNull(launch.Pid);
        Assert.True(launch.Pid!.Value > 0);

        try
        {
            // the dialog needs a moment to appear on the isolated desktop
            DesktopWindowInfo? row = null;
            for (var attempt = 0; attempt < 40 && row is null; attempt++)
            {
                var list = runtime.ListWindows(new DesktopListWindowsArgs { Mode = DesktopToolRuntime.IsolatedMode, Limit = 200 });
                Assert.True(list.Ok, list.Message);
                row = list.Windows?.FirstOrDefault(w => w.Pid == launch.Pid);
                if (row is null) Thread.Sleep(150);
            }
            Assert.NotNull(row);
            Assert.NotNull(row!.Bounds);
            Assert.True(row.Bounds!.Width > 0);

            // E8 follow-up: the launched window must never leak onto the user's (default) desktop
            var defaultList = runtime.ListWindows(new DesktopListWindowsArgs { Limit = 200 });
            Assert.True(defaultList.Ok, defaultList.Message);
            Assert.DoesNotContain(defaultList.Windows!, w => w.Pid == launch.Pid);

            var shot = runtime.Screenshot(new DesktopScreenshotArgs { WindowId = row.WindowId, Mode = DesktopToolRuntime.IsolatedMode });
            Assert.True(shot.Ok, shot.Message);
            Assert.True(File.Exists(shot.Path));
            Assert.True(shot.Width > 0 && shot.Height > 0);

            var text = runtime.ReadText(new DesktopReadTextArgs { WindowId = row.WindowId, Mode = DesktopToolRuntime.IsolatedMode });
            Assert.True(text.Ok, text.Message);
            Assert.NotNull(text.Texts);
            Assert.Contains(text.Texts!, t => !string.IsNullOrEmpty(t.Text));
        }
        finally
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(launch.Pid!.Value);
                process.Kill();
            }
            catch (ArgumentException)
            {
                // already gone
            }
        }
    }

    [Fact]
    public void LaunchApp_RejectsEmptyCommand()
    {
        var runtime = new DesktopToolRuntime(() => TempHome());
        var result = runtime.LaunchApp(new DesktopLaunchArgs { Command = " " });
        Assert.False(result.Ok);
        Assert.Equal("desktop_input_failed", result.Code);
    }

    // ── manifest ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Manifest_IncludesDesktopToolsWithGovernanceFlags()
    {
        DesktopToolRegistration.Register();
        var response = await ToolRegistry.DispatchAsync(new ToolCallRequest<JsonElement>
        {
            ToolId = "#manifest",
            SessionId = "test",
            ToolCallId = 1,
            Approved = false,
        });
        Assert.True(response.IsSuccess);

        using var document = JsonDocument.Parse(response.Response.GetRawText());
        var tools = document.RootElement.GetProperty("tools").EnumerateArray()
            .ToDictionary(t => t.GetProperty("id").GetString()!, t => t, StringComparer.OrdinalIgnoreCase);

        Assert.Contains(DesktopToolRegistration.ListWindowsToolId, tools);
        var screenshot = tools[DesktopToolRegistration.ScreenshotToolId];
        Assert.False(screenshot.GetProperty("requires_approval").GetBoolean());
        Assert.Equal("low", screenshot.GetProperty("risk").GetString());
        Assert.Equal("safe", screenshot.GetProperty("retry_safety").GetString());

        foreach (var actionTool in new[] { DesktopToolRegistration.ClickToolId, DesktopToolRegistration.TypeToolId, DesktopToolRegistration.PressKeyToolId, DesktopToolRegistration.LaunchAppToolId })
        {
            var tool = tools[actionTool];
            Assert.True(tool.GetProperty("requires_approval").GetBoolean(), actionTool);
            Assert.Equal("high", tool.GetProperty("risk").GetString(), ignoreCase: true);
            Assert.Equal("unsafe", tool.GetProperty("retry_safety").GetString());
        }
    }

    // ── a real top-level window with an EDIT child on a pumping thread ─────────

    private sealed class TestWindow : IDisposable
    {
        private static readonly WndProcDelegate Procedure = Proc;

        public nint Hwnd { get; private set; }
        public nint Edit { get; private set; }

        private readonly Thread _thread;
        private readonly ManualResetEvent _ready = new(false);
        private readonly int _width;
        private readonly int _height;

        public TestWindow(int width = 320, int height = 200)
        {
            _width = width;
            _height = height;
            _thread = new Thread(Run) { IsBackground = true };
            _thread.Start();
            Assert.True(_ready.WaitOne(TimeSpan.FromSeconds(5)), "test window thread did not start");
        }

        public static void Minimize(nint hWnd) => ShowWindow(hWnd, SW_MINIMIZE);
        public static void Restore(nint hWnd) => ShowWindow(hWnd, SW_RESTORE);

        private void Run()
        {
            var className = "TinadecDeskTest_" + Guid.NewGuid().ToString("N");
            var wc = new WNDCLASS { lpfnWndProc = Procedure, LpszClassName = className, HInstance = GetModuleHandle(null) };
            RegisterClass(ref wc);

            Hwnd = CreateWindowEx(
                WS_EX_TOPMOST, className, "Tinadec desktop tool test window", WS_OVERLAPPEDWINDOW | WS_VISIBLE,
                60, 60, _width, _height, 0, 0, GetModuleHandle(null), 0);
            Edit = CreateWindowEx(
                WS_EX_CLIENTEDGE, "EDIT", "", WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL,
                10, 10, Math.Max(50, _width - 40), 40, Hwnd, 0, GetModuleHandle(null), 0);

            ShowWindow(Hwnd, SW_SHOW);
            UpdateWindow(Hwnd);
            _ready.Set();

            while (GetMessage(out var msg, 0, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }

        public void Dispose()
        {
            PostMessage(Hwnd, WM_CLOSE, 0, 0);
            if (!_thread.Join(TimeSpan.FromSeconds(3))) throw new InvalidOperationException("test window thread did not exit");
            _ready.Dispose();
        }

        private static nint Proc(nint hWnd, uint msg, nint wParam, nint lParam)
        {
            if (msg == WM_DESTROY)
            {
                PostQuitMessage(0);
                return 0;
            }
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private const uint WM_DESTROY = 0x0002;
        private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
        private const uint WS_VISIBLE = 0x10000000;
        private const uint WS_EX_TOPMOST = 0x00000008;
        private const uint WS_CHILD = 0x40000000;
        private const uint WS_EX_CLIENTEDGE = 0x00000200;
        private const uint ES_AUTOHSCROLL = 0x0080;
        private const int SW_SHOW = 5;
        private const int SW_MINIMIZE = 6;
        private const int SW_RESTORE = 9;
        private const uint WM_CLOSE = 0x0010;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

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
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
        [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int command);
        [DllImport("user32.dll")] private static extern bool UpdateWindow(nint hWnd);
        [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, nint hWnd, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] private static extern nint DispatchMessage(ref MSG msg);
        [DllImport("user32.dll")] private static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);
        [DllImport("user32.dll")] private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);
        [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    }
}
