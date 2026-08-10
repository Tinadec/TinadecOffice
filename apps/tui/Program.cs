using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace TinadecOffice.Tui;

/// <summary>
/// TUI 客户端引导。
///
/// 采用 Terminal.Gui v2 实例式 API：Application.Create() 是静态工厂，
/// 返回的 IApplication 实例负责 Init / Run / 释放（Dispose 即 v1 的 Shutdown）。
/// </summary>
internal static class Program
{
    private static void Main()
    {
        // Ctrl+Q 退出（Esc 默认已绑定 Command.Quit，这里补一个直觉键位）。
        // 必须在 Init() 之前设置。
        Application.SetDefaultKeyBinding(Command.Quit, Bind.All(Key.Q.WithCtrl));

        using IApplication app = Application.Create();
        // Windows Terminal 支持 TrueColor；v2.4.17 在 Win32NT 的默认驱动本就是 ANSI，显式固定以保持一致。
        app.ForceDriver = DriverRegistry.Names.ANSI;
        app.Init();

        app.Run<WelcomePage>();
    }
}
