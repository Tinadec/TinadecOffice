using Terminal.Gui.Drawing;
// 消除 System.Attribute（ImplicitUsings 引入）与 Terminal.Gui.Drawing.Attribute 的歧义。
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace TinadecOffice.Tui;

/// <summary>
/// TUI 深色主题。颜色取自桌面暗色板（apps/desktop/src/styles.css）：
/// bg #0a0e14、text #c9d1d9、secondary #7d8590、accent #2ec4b6。
///
/// v2 里没有 v1 的 ColorScheme/Colors，改用 Scheme（不可变 record）。
/// 未显式指定的角色（Focus/HotNormal/Disabled 等）会从 Normal 自动派生。
/// </summary>
internal static class AppTheme
{
    private static readonly Color Background = new(0x0a, 0x0e, 0x14);
    private static readonly Color Text = new(0xc9, 0xd1, 0xd9);
    private static readonly Color TextSecondary = new(0x7d, 0x85, 0x90);
    private static readonly Color Accent = new(0x2e, 0xc4, 0xb6);
    private static readonly Color Success = new(0x3f, 0xb9, 0x50);
    private static readonly Color Danger = new(0xf8, 0x51, 0x49);

    /// 欢迎页主方案：深色底、浅色正文、青色强调。
    public static Scheme Welcome { get; } = new()
    {
        Normal = new Attribute(Text, Background),
        HotNormal = new Attribute(Accent, Background),
        Focus = new Attribute(Background, Accent),
        HotFocus = new Attribute(Background, Accent),
        Highlight = new Attribute(Accent, Background, TextStyle.Bold),
    };

    /// 品牌行：青色粗体。
    public static Scheme Brand { get; } = new()
    {
        Normal = new Attribute(Accent, Background, TextStyle.Bold),
    };

    /// 副标题：次要色。
    public static Scheme Subtitle { get; } = new()
    {
        Normal = new Attribute(TextSecondary, Background),
    };

    /// 健康状态的绿/红前景，背景随父级继承。
    public static Scheme Healthy { get; } = new()
    {
        Normal = new Attribute(Success, Background),
    };

    public static Scheme Unhealthy { get; } = new()
    {
        Normal = new Attribute(Danger, Background),
    };
}
