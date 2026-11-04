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

        // 2. window screenshot: PNG lands on disk with the window's physical size
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

        // A client point far outside the window: client (5000, 5000) maps to a screen
        // point that is not on the window, so nothing must be sent.
        var result = await runtime.ClickAsync(new DesktopClickArgs
        {
            X = 5000, Y = 5000, WindowId = window.Hwnd, Mode = DesktopToolRuntime.SilentMode,
        });
        Assert.False(result.Ok);
        Assert.Equal("desktop_post_failed", result.Code);
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

        foreach (var actionTool in new[] { DesktopToolRegistration.ClickToolId, DesktopToolRegistration.TypeToolId, DesktopToolRegistration.PressKeyToolId })
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

        public TestWindow()
        {
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
                60, 60, 320, 200, 0, 0, GetModuleHandle(null), 0);
            Edit = CreateWindowEx(
                WS_EX_CLIENTEDGE, "EDIT", "", WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL,
                10, 10, 280, 40, Hwnd, 0, GetModuleHandle(null), 0);

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
