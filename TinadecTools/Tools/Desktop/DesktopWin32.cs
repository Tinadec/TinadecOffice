using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace TinadecTools.Tools.Desktop;

/// <summary>
/// The complete Win32 surface behind the desktop tools: window enumeration, GDI capture
/// (screen BitBlt and per-window PrintWindow), real input injection (SendInput) for the
/// fast mode, and message-direct writes (PostMessage / SendMessageTimeout) for the
/// silent mode. Everything is plain P/Invoke with no COM and no System.Drawing, so the
/// AOT-published tool host stays dependency-free.
///
/// Geometry, capture and cursor coordinates are physical pixels: every runtime operation
/// runs inside a <see cref="PerMonitorDpiScope"/> so GetWindowRect/ClientToScreen report
/// unscaled values regardless of the host process's DPI awareness.
/// </summary>
internal static class DesktopWin32
{
    // ── constants ──────────────────────────────────────────────────────────────

    internal const uint PW_RENDERFULLCONTENT = 2;
    internal const int SRCCOPY = 0x00CC0020;
    internal const uint WM_GETTEXT = 0x000D;
    internal const uint WM_GETTEXTLENGTH = 0x000E;
    internal const uint WM_CHAR = 0x0102;
    internal const uint WM_CLOSE = 0x0010;
    internal const uint WM_LBUTTONDOWN = 0x0201;
    internal const uint WM_LBUTTONUP = 0x0202;
    internal const uint WM_RBUTTONDOWN = 0x0204;
    internal const uint WM_RBUTTONUP = 0x0205;
    internal const uint WM_MBUTTONDOWN = 0x0207;
    internal const uint WM_MBUTTONUP = 0x0208;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0040;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0080;
    private const int GA_ROOT = 2;
    private const int SW_RESTORE = 9;
    private static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    // ── interop types ──────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint DwFlags;
        public uint Time;
        public nint DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort WVk;
        public ushort WScan;
        public uint DwFlags;
        public uint Time;
        public nint DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HARDWAREINPUT
    {
        public uint UMsg;
        public ushort WParamL;
        public ushort WParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mi;
        [FieldOffset(0)] public KEYBDINPUT Ki;
        [FieldOffset(0)] public HARDWAREINPUT Hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint BiSize;
        public int BiWidth;
        public int BiHeight;
        public ushort BiPlanes;
        public ushort BiBitCount;
        public uint BiCompression;
        public uint BiSizeImage;
        public int BiXPelsPerMeter;
        public int BiYPelsPerMeter;
        public uint BiClrUsed;
        public uint BiClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER Header;
        public uint Colors;
    }

    // ── delegates ──────────────────────────────────────────────────────────────

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    // ── windowing ──────────────────────────────────────────────────────────────

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, nint lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint hWnd, EnumWindowsProc proc, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hWnd, StringBuilder className, int maxCount);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint hWnd, ref POINT point);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hWnd, ref POINT point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hWnd, int flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int command);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint inputCount, INPUT[] inputs, int inputSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint SendMessageTimeout(nint hWnd, uint msg, nint wParam, StringBuilder lParam, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint SendMessageTimeout(nint hWnd, uint msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);

    // ── GDI ────────────────────────────────────────────────────────────────────

    [DllImport("user32.dll")] private static extern nint GetWindowDC(nint hWnd);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hWnd, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sourceX, int sourceY, int rop);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint hWnd, nint dc, uint flags);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint startScan, uint scanLines, byte[] bits, ref BITMAPINFO info, uint usage);

    /// <summary>One visible top-level window as reported by <see cref="ListTopLevelWindows"/>.</summary>
    internal sealed record WindowRow(
        nint Handle,
        uint ProcessId,
        string ProcessName,
        string Title,
        string ClassName,
        int X, int Y, int Width, int Height,
        bool Foreground,
        bool Minimized,
        bool HasTitle);

    /// <summary>
    /// Enters per-monitor-v2 DPI awareness for the calling thread and restores the previous
    /// context on dispose, so window geometry and cursor coordinates are physical pixels.
    /// </summary>
    internal readonly struct PerMonitorDpiScope : IDisposable
    {
        private readonly nint _previous;

        public static PerMonitorDpiScope Enter() => new(SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2));

        private PerMonitorDpiScope(nint previous) => _previous = previous;

        public void Dispose()
        {
            if (_previous != 0) SetThreadDpiAwarenessContext(_previous);
        }
    }

    internal static nint GetForeground() => GetForegroundWindow();

    internal static bool IsMinimized(nint hWnd) => IsIconic(hWnd);

    internal static bool IsEnabled(nint hWnd) => IsWindowEnabled(hWnd);

    internal static uint ThreadIdOf(nint hWnd, out uint processId) => GetWindowThreadProcessId(hWnd, out processId);

    internal static (int X, int Y, int Width, int Height) WindowBounds(nint hWnd)
    {
        GetWindowRect(hWnd, out var rect);
        return (rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    internal static (int X, int Y, int Width, int Height) ClientBounds(nint hWnd)
    {
        GetClientRect(hWnd, out var rect);
        return (rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    /// <summary>Converts client coordinates of <paramref name="hWnd"/> to screen coordinates.</summary>
    internal static POINT ClientToScreen(nint hWnd, int x, int y)
    {
        var point = new POINT { X = x, Y = y };
        ClientToScreen(hWnd, ref point);
        return point;
    }

    /// <summary>Converts screen coordinates to client coordinates of <paramref name="hWnd"/>.</summary>
    internal static POINT ScreenToClient(nint hWnd, int x, int y)
    {
        var point = new POINT { X = x, Y = y };
        ScreenToClient(hWnd, ref point);
        return point;
    }

    internal static (int X, int Y, int Width, int Height) VirtualScreen() =>
        (GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));

    internal static string WindowTitle(nint hWnd)
    {
        var length = GetWindowTextLength(hWnd);
        if (length <= 0) return string.Empty;
        var builder = new StringBuilder(length + 1);
        GetWindowText(hWnd, builder, builder.Capacity);
        return builder.ToString();
    }

    internal static string WindowClass(nint hWnd)
    {
        var builder = new StringBuilder(256);
        GetClassName(hWnd, builder, builder.Capacity);
        return builder.ToString();
    }

    /// <summary>
    /// Enumerates top-level windows in Z order. By default only visible, titled, uncloaked
    /// windows are returned; <paramref name="includeAll"/> keeps every top-level window.
    /// </summary>
    internal static List<WindowRow> ListTopLevelWindows(bool includeAll, int limit)
    {
        var rows = new List<WindowRow>();
        EnumWindows((hWnd, _) =>
        {
            var visible = IsWindowVisible(hWnd);
            if (!includeAll && !visible) return true;
            var title = WindowTitle(hWnd);
            if (!includeAll && title.Length == 0) return true;
            if (DwmGetWindowAttribute(hWnd, 14 /* DWMWA_CLOAKED */, out var cloaked, sizeof(int)) == 0 && cloaked != 0
                && !includeAll)
                return true;
            GetWindowThreadProcessId(hWnd, out var pid);
            GetWindowRect(hWnd, out var rect);
            rows.Add(new WindowRow(
                hWnd,
                pid,
                ProcessNameOf(pid),
                title,
                WindowClass(hWnd),
                rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
                GetForegroundWindow() == hWnd,
                IsIconic(hWnd),
                title.Length > 0));
            return rows.Count < 4096;
        }, 0);
        return limit > 0 ? rows.Take(limit).ToList() : rows;
    }

    /// <summary>Descendant windows of <paramref name="hWnd"/> in enumeration order.</summary>
    internal static List<nint> ChildWindows(nint hWnd, int limit)
    {
        var children = new List<nint>();
        EnumChildWindows(hWnd, (child, _) =>
        {
            children.Add(child);
            return children.Count < limit;
        }, 0);
        return children;
    }

    private static string ProcessNameOf(uint pid)
    {
        var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (process == 0) return string.Empty;
        try
        {
            var builder = new StringBuilder(1024);
            var size = (uint)builder.Capacity;
            return QueryFullProcessImageName(process, 0, builder, ref size) ? builder.ToString() : string.Empty;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    // ── capture ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Captures one window with PrintWindow(PW_RENDERFULLCONTENT): the compositor renders
    /// the window even when it is fully occluded, which is what makes silent-mode operation
    /// possible without stealing focus. A minimized window has no surface and is rejected.
    /// Returns the raw 32bpp BGRA bitmap so the caller can encode both the full PNG and a
    /// downscaled context copy from one capture.
    /// </summary>
    internal static (byte[] Bgra, int Width, int Height) CaptureWindowBits(nint hWnd)
    {
        GetWindowRect(hWnd, out var rect);
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) throw new InvalidOperationException("window has no surface");
        var bits = new byte[width * height * 4];
        var hdcWindow = GetWindowDC(hWnd);
        var hdcMemory = nint.Zero;
        var bitmap = nint.Zero;
        try
        {
            hdcMemory = CreateCompatibleDC(hdcWindow);
            bitmap = CreateCompatibleBitmap(hdcWindow, width, height);
            SelectObject(hdcMemory, bitmap);
            if (!PrintWindow(hWnd, hdcMemory, PW_RENDERFULLCONTENT))
            {
                // One retry: some DWM windows fail their first render after a resize.
                if (!PrintWindow(hWnd, hdcMemory, PW_RENDERFULLCONTENT))
                    throw new InvalidOperationException("PrintWindow failed");
            }
            ReadBitmapBits(hdcMemory, bitmap, width, height, bits);
        }
        finally
        {
            if (bitmap != 0) DeleteObject(bitmap);
            if (hdcMemory != 0) DeleteDC(hdcMemory);
            if (hdcWindow != 0) ReleaseDC(hWnd, hdcWindow);
        }
        return (bits, width, height);
    }

    internal static byte[] CaptureWindowPng(nint hWnd)
    {
        var (bits, width, height) = CaptureWindowBits(hWnd);
        return PngEncoder.Encode(bits, width, height);
    }

    /// <summary>
    /// Captures the whole virtual desktop (all monitors) with BitBlt, as raw 32bpp BGRA.
    /// </summary>
    internal static (byte[] Bgra, int Width, int Height) CaptureScreenBits()
    {
        var (x, y, width, height) = VirtualScreen();
        if (width <= 0 || height <= 0) throw new InvalidOperationException("no virtual screen");
        var bits = new byte[width * height * 4];
        var hdcScreen = GetDC(0);
        var hdcMemory = nint.Zero;
        var bitmap = nint.Zero;
        try
        {
            hdcMemory = CreateCompatibleDC(hdcScreen);
            bitmap = CreateCompatibleBitmap(hdcScreen, width, height);
            SelectObject(hdcMemory, bitmap);
            if (!BitBlt(hdcMemory, 0, 0, width, height, hdcScreen, x, y, SRCCOPY))
                throw new InvalidOperationException("BitBlt failed");
            ReadBitmapBits(hdcMemory, bitmap, width, height, bits);
        }
        finally
        {
            if (bitmap != 0) DeleteObject(bitmap);
            if (hdcMemory != 0) DeleteDC(hdcMemory);
            if (hdcScreen != 0) ReleaseDC(0, hdcScreen);
        }
        return (bits, width, height);
    }

    internal static byte[] CaptureScreenPng()
    {
        var (bits, width, height) = CaptureScreenBits();
        return PngEncoder.Encode(bits, width, height);
    }

    /// <summary>
    /// Box-average downscale of a 32bpp BGRA top-down bitmap so the long edge stays within
    /// <paramref name="maxDimension"/>. Pure managed arithmetic (AOT-safe, no System.Drawing):
    /// every destination pixel averages the source box it covers, which keeps UI text legible
    /// far better than point sampling. Returns the input unchanged when no shrink is needed.
    /// </summary>
    internal static (byte[] Bgra, int Width, int Height) Downscale(byte[] bgra, int width, int height, int maxDimension)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        var longEdge = Math.Max(width, height);
        if (longEdge <= maxDimension) return (bgra, width, height);
        var scale = (double)maxDimension / longEdge;
        var newWidth = Math.Max(1, (int)Math.Round(width * scale));
        var newHeight = Math.Max(1, (int)Math.Round(height * scale));
        var result = new byte[newWidth * newHeight * 4];
        for (var y = 0; y < newHeight; y++)
        {
            // the source rows this destination row covers
            var rowStart = (int)((long)y * height / newHeight);
            var rowEnd = Math.Max(rowStart + 1, (int)((long)(y + 1) * height / newHeight));
            for (var x = 0; x < newWidth; x++)
            {
                var columnStart = (int)((long)x * width / newWidth);
                var columnEnd = Math.Max(columnStart + 1, (int)((long)(x + 1) * width / newWidth));
                long blue = 0, green = 0, red = 0, samples = 0;
                for (var sy = rowStart; sy < rowEnd && sy < height; sy++)
                {
                    var row = sy * width * 4;
                    for (var sx = columnStart; sx < columnEnd && sx < width; sx++)
                    {
                        var pixel = row + sx * 4;
                        blue += bgra[pixel];
                        green += bgra[pixel + 1];
                        red += bgra[pixel + 2];
                        samples++;
                    }
                }
                var target = (y * newWidth + x) * 4;
                result[target] = (byte)(blue / samples);
                result[target + 1] = (byte)(green / samples);
                result[target + 2] = (byte)(red / samples);
                result[target + 3] = 255;
            }
        }
        return (result, newWidth, newHeight);
    }

    private static void ReadBitmapBits(nint dc, nint bitmap, int width, int height, byte[] bits)
    {
        var info = new BITMAPINFO
        {
            Header = new BITMAPINFOHEADER
            {
                BiSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                BiWidth = width,
                BiHeight = -height, // negative height = top-down rows
                BiPlanes = 1,
                BiBitCount = 32,
                BiCompression = 0, // BI_RGB
            },
        };
        if (GetDIBits(dc, bitmap, 0, (uint)height, bits, ref info, 0) == 0)
            throw new InvalidOperationException("GetDIBits failed");
    }

    // ── input: fast mode (real input) ──────────────────────────────────────────

    /// <summary>Moves the physical cursor and sends real mouse button events.</summary>
    internal static void ClickAt(int screenX, int screenY, string button, int count)
    {
        if (!SetCursorPos(screenX, screenY)) throw new InvalidOperationException("SetCursorPos failed");
        var (down, up) = button switch
        {
            "right" => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            "middle" => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
        };
        for (var i = 0; i < count; i++)
        {
            SendMouse(down);
            SendMouse(up);
        }
    }

    private static void SendMouse(uint flags)
    {
        var input = new INPUT { Type = 0 /* INPUT_MOUSE */ };
        input.U.Mi = new MOUSEINPUT { DwFlags = flags };
        if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) != 1)
            throw new InvalidOperationException("SendInput rejected the mouse event (UIPI or blocked input)");
    }

    /// <summary>Sends literal text as Unicode keyboard events, one UTF-16 code unit at a time.</summary>
    internal static void TypeUnicode(string text)
    {
        foreach (var unit in text)
        {
            SendKey(new KEYBDINPUT { WScan = unit, DwFlags = KEYEVENTF_UNICODE });
            SendKey(new KEYBDINPUT { WScan = unit, DwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP });
        }
    }

    /// <summary>Sends a key or chord such as "ctrl+a" as real keyboard events.</summary>
    internal static void PressKeys(ushort[] virtualKeys)
    {
        foreach (var key in virtualKeys) SendKey(new KEYBDINPUT { WVk = key, DwFlags = 0 });
        for (var i = virtualKeys.Length - 1; i >= 0; i--)
            SendKey(new KEYBDINPUT { WVk = virtualKeys[i], DwFlags = KEYEVENTF_KEYUP });
    }

    private static void SendKey(KEYBDINPUT key)
    {
        var input = new INPUT { Type = 1 /* INPUT_KEYBOARD */ };
        input.U.Ki = key;
        if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) != 1)
            throw new InvalidOperationException("SendInput rejected the key event (UIPI or blocked input)");
    }

    /// <summary>
    /// Brings a window to the foreground, restoring it first when minimized. Uses the
    /// AttachThreadInput workaround for the foreground lock so a background process can
    /// still take focus for typing. Returns false when the OS refuses the focus change.
    /// </summary>
    internal static bool FocusWindow(nint hWnd)
    {
        if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
        var foreground = GetForegroundWindow();
        if (foreground == hWnd) return true;
        var foregroundThread = foreground != 0 ? GetWindowThreadProcessId(foreground, out _) : 0;
        var currentThread = GetCurrentThreadId();
        var attached = false;
        if (foregroundThread != 0 && foregroundThread != currentThread)
            attached = AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached) AttachThreadInput(currentThread, foregroundThread, false);
        }
        return GetForegroundWindow() == hWnd;
    }

    // ── input: silent mode (message-direct writes) ─────────────────────────────

    /// <summary>
    /// Resolves the window that would receive a click at a screen point and verifies it
    /// belongs to the expected top-level window. Returns the receiver and the point in its
    /// client coordinates, or null when the point is not on the target window.
    /// </summary>
    internal static (nint Receiver, POINT Client)? ResolveClickTarget(nint root, int screenX, int screenY)
    {
        var receiver = WindowFromPoint(new POINT { X = screenX, Y = screenY });
        if (receiver == 0) return null;
        var receiverRoot = GetAncestor(receiver, GA_ROOT);
        if (receiverRoot != root && receiver != root) return null;
        var client = ScreenToClient(receiver, screenX, screenY);
        return (receiver, client);
    }

    /// <summary>
    /// The top-level window that actually occupies a screen point, with its class and title.
    /// The pre-click occlusion guard compares this against the intended target: a full-screen
    /// topmost overlay (measured: an orphaned Shell_SystemDim dimming layer) silently steals
    /// the click, and clicking "OK/Allow" on whatever is on top is an accident, not a miss.
    /// </summary>
    internal static (nint Root, string ClassName, string Title) WindowRootAtPoint(int screenX, int screenY)
    {
        var hit = WindowFromPoint(new POINT { X = screenX, Y = screenY });
        var root = hit == 0 ? 0 : GetAncestor(hit, GA_ROOT);
        return (root, root == 0 ? "" : WindowClass(root), root == 0 ? "" : WindowTitle(root));
    }

    /// <summary>The client-area size of a window, in that window's own units.</summary>
    internal static (int Width, int Height) ClientSize(nint hWnd)
    {
        GetClientRect(hWnd, out var rect);
        return (rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    /// <summary>Posts a mouse press/release pair to a window with client coordinates.</summary>
    internal static void PostClick(nint hWnd, POINT client, string button)
    {
        var (down, up) = button switch
        {
            "right" => (WM_RBUTTONDOWN, WM_RBUTTONUP),
            "middle" => (WM_MBUTTONDOWN, WM_MBUTTONUP),
            _ => (WM_LBUTTONDOWN, WM_LBUTTONUP),
        };
        var lParam = (nint)((client.Y << 16) | (client.X & 0xFFFF));
        if (!PostMessage(hWnd, down, 0, lParam)) throw new InvalidOperationException("PostMessage(WM_xBUTTONDOWN) failed");
        if (!PostMessage(hWnd, up, 0, lParam)) throw new InvalidOperationException("PostMessage(WM_xBUTTONUP) failed");
    }

    /// <summary>Posts literal text as WM_CHAR code units. No IME composition is involved.</summary>
    internal static void PostChars(nint hWnd, string text)
    {
        foreach (var unit in text)
        {
            if (!PostMessage(hWnd, WM_CHAR, unit, 0))
                throw new InvalidOperationException("PostMessage(WM_CHAR) failed");
        }
    }

    /// <summary>Posts a key-down/key-up pair for a single virtual key.</summary>
    internal static void PostKey(nint hWnd, ushort virtualKey)
    {
        if (!PostMessage(hWnd, 0x0100 /* WM_KEYDOWN */, virtualKey, 0)) throw new InvalidOperationException("PostMessage(WM_KEYDOWN) failed");
        if (!PostMessage(hWnd, 0x0101 /* WM_KEYUP */, virtualKey, 0)) throw new InvalidOperationException("PostMessage(WM_KEYUP) failed");
    }

    /// <summary>
    /// Reads window text with SendMessageTimeout(WM_GETTEXT) so an unresponsive target can
    /// only cost the timeout, never hang the tool host. Returns an empty string when the
    /// window has no text.
    /// </summary>
    internal static string ReadWindowText(nint hWnd, int maxChars, uint timeoutMs)
    {
        var length = SendMessageTimeout(hWnd, WM_GETTEXTLENGTH, 0, 0, SMTO_ABORTIFHUNG, timeoutMs, out var lengthResult) != 0
            ? (int)lengthResult
            : 0;
        if (length <= 0) return string.Empty;
        if (length > maxChars) length = maxChars;
        var builder = new StringBuilder(length + 1);
        SendMessageTimeout(hWnd, WM_GETTEXT, builder.Capacity, builder, SMTO_ABORTIFHUNG, timeoutMs, out _);
        return builder.ToString();
    }

    // ── isolated desktop (hidden desktop in the same session) ─────────────────

    private const uint DesktopAllAccess = 0x01FF;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenDesktop(string desktop, uint flags, bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateDesktop(string desktop, string? device, nint devmode, uint flags, uint access, nint sa);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(nint desktop);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public uint cb;
        public string? lpReserved;
        public nint lpDesktop;
        public string? lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public nint hProcess, hThread; public uint dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(nint appName, nint commandLine, nint processSa, nint threadSa, bool inheritHandles, uint creationFlags, nint environment, string? currentDirectory, ref STARTUPINFOW startup, out PROCESS_INFORMATION processInfo);

    /// <summary>
    /// The named hidden desktop used by isolated mode. Created on first use, reopened after.
    /// Probe-measured (evidence/probe-*.log E1/E5): DWM composes its windows,
    /// PrintWindow(PW_RENDERFULLCONTENT) captures them, PostMessage reaches them, and the
    /// clipboard is shared with the default desktop through the common window station.
    /// </summary>
    internal const string IsolatedDesktopName = "TinadecIsolated";

    internal static nint OpenOrCreateIsolatedDesktop()
    {
        var desk = OpenDesktop(IsolatedDesktopName, 0, false, DesktopAllAccess);
        if (desk == 0) desk = CreateDesktop(IsolatedDesktopName, null, nint.Zero, 0, DesktopAllAccess, nint.Zero);
        return desk;
    }

    /// <summary>
    /// Runs an action on a fresh MTA thread attached to the isolated desktop. HWNDs are
    /// desktop-scoped, so every window operation against the isolated desktop must run inside
    /// this. MTA is required: the CLR initializes COM on STA threads at start-up and the STA
    /// OLE window pins the thread to its current desktop — SetThreadDesktop then fails with
    /// ERROR_BUSY (probe-measured).
    /// </summary>
    internal static T RunOnIsolatedDesktop<T>(Func<T> action, out string? error)
    {
        T result = default!;
        string? failure = null;
        var thread = new Thread(() =>
        {
            var desk = OpenOrCreateIsolatedDesktop();
            if (desk == 0)
            {
                failure = $"OpenDesktop/CreateDesktop('{IsolatedDesktopName}') failed err={Marshal.GetLastWin32Error()}";
                return;
            }
            try
            {
                if (!SetThreadDesktop(desk))
                {
                    failure = $"SetThreadDesktop failed err={Marshal.GetLastWin32Error()}";
                    return;
                }
                result = action();
            }
            catch (Exception exception)
            {
                failure = exception.Message;
            }
        });
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        error = failure;
        return result;
    }

    /// <summary>
    /// Launches a process onto the isolated desktop (STARTUPINFO.lpDesktop). The desktop must
    /// be named explicitly: a null lpDesktop inherits the parent PROCESS's desktop, not the
    /// calling thread's (probe-measured, E8) — which is the user-visible Default desktop, so a
    /// launch without a desktop name is refused outright instead of ever putting a window in
    /// front of the user.
    /// </summary>
    internal static uint LaunchOnIsolatedDesktop(string command, string? arguments, string? workingDirectory, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(IsolatedDesktopName))
        {
            // Defensive: the name is a constant today, but a launch that somehow reaches this
            // path without one would land on the Default desktop — in the user's face.
            error = "isolated desktop name is empty; refusing to launch (a null lpDesktop inherits the parent process's desktop, which is the user's)";
            return 0;
        }
        var desk = OpenOrCreateIsolatedDesktop();
        if (desk == 0)
        {
            error = $"OpenDesktop/CreateDesktop('{IsolatedDesktopName}') failed err={Marshal.GetLastWin32Error()}";
            return 0;
        }

        var commandLine = Marshal.StringToHGlobalUni(arguments is { Length: > 0 } ? $"\"{command}\" {arguments}" : $"\"{command}\"");
        var desktop = Marshal.StringToHGlobalUni(IsolatedDesktopName);
        try
        {
            var startup = default(STARTUPINFOW);
            startup.cb = (uint)Marshal.SizeOf<STARTUPINFOW>();
            startup.lpDesktop = desktop;
            if (!CreateProcessW(nint.Zero, commandLine, nint.Zero, nint.Zero, false, 0x08000000 /* CREATE_NO_WINDOW */, nint.Zero,
                    workingDirectory, ref startup, out var info))
            {
                error = $"CreateProcessW failed err={Marshal.GetLastWin32Error()}";
                return 0;
            }
            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
            return info.dwProcessId;
        }
        finally
        {
            Marshal.FreeHGlobal(commandLine);
            Marshal.FreeHGlobal(desktop);
        }
    }

    // ── PNG encoder ────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal PNG encoder for 32bpp BGRA top-down bitmaps: filter-0 scanlines, one IDAT
    /// zlib stream over System.IO.Compression, hand-rolled CRC-32 and Adler-32. Exists
    /// because the tool host is AOT-published without System.Drawing.
    /// </summary>
    internal static class PngEncoder
    {
        public static byte[] Encode(ReadOnlySpan<byte> bgra, int width, int height)
        {
            var stride = width * 4;
            var raw = new byte[height * (width * 3 + 1)];
            var rawOffset = 0;
            for (var y = 0; y < height; y++)
            {
                raw[rawOffset++] = 0; // filter type: none
                var row = y * stride;
                for (var x = 0; x < width; x++)
                {
                    var pixel = row + x * 4;
                    raw[rawOffset++] = bgra[pixel + 2]; // R
                    raw[rawOffset++] = bgra[pixel + 1]; // G
                    raw[rawOffset++] = bgra[pixel];     // B
                }
            }

            using var compressed = new MemoryStream();
            using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(raw, 0, raw.Length);
            }
            var zlibBody = compressed.ToArray();

            var output = new MemoryStream();
            output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]); // signature
            WriteChunk(output, "IHDR", Ihdr(width, height));
            var zlib = new byte[zlibBody.Length + 6];
            zlib[0] = 0x78;
            zlib[1] = 0x01;
            zlibBody.AsSpan().CopyTo(zlib.AsSpan(2));
            WriteUInt32BigEndian(zlib, zlib.Length - 4, Adler32(raw));
            WriteChunk(output, "IDAT", zlib);
            WriteChunk(output, "IEND", []);
            return output.ToArray();
        }

        private static byte[] Ihdr(int width, int height)
        {
            var header = new byte[13];
            WriteUInt32BigEndian(header, 0, (uint)width);
            WriteUInt32BigEndian(header, 4, (uint)height);
            header[8] = 8;  // bit depth
            header[9] = 2;  // color type: truecolor RGB
            return header;
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            var header = new byte[8];
            WriteUInt32BigEndian(header, 0, (uint)data.Length);
            var typeBytes = Encoding.ASCII.GetBytes(type);
            typeBytes.CopyTo(header, 4);
            stream.Write(header);
            stream.Write(data);
            var crc = new byte[4];
            // The chunk CRC covers the type bytes exactly once, then the data. The previous
            // helper concatenated the type twice (it copied header[4..8] — which already is the
            // type — and then appended typeBytes again), so every chunk carried the CRC of
            // "IENDIEND"-style doubled input: GDI+ tolerates a bad chunk CRC, but strict
            // decoders on model-provider pipelines reject the whole image.
            var value = Crc32Continue(0, typeBytes);
            value = Crc32Continue(value, data);
            WriteUInt32BigEndian(crc, 0, value);
            stream.Write(crc);
        }

        private static void WriteUInt32BigEndian(byte[] target, int offset, uint value)
        {
            target[offset] = (byte)(value >> 24);
            target[offset + 1] = (byte)(value >> 16);
            target[offset + 2] = (byte)(value >> 8);
            target[offset + 3] = (byte)value;
        }

        private static uint Crc32Continue(uint crc, byte[] data)
        {
            crc = ~crc;
            foreach (var value in data)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
            return ~crc;
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (var value in data)
            {
                a = (a + value) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }
    }
}
