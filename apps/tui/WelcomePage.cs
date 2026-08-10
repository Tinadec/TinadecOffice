using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace TinadecOffice.Tui;

/// <summary>
/// 欢迎页：品牌 + 输入框 + 动作按钮 + 底部后端健康状态。
/// 信息架构映射桌面 WelcomeScreen.vue（不复制其视觉）。
///
/// 动作按钮第一版为占位：Core invocation 写路径仍是 501 stub，此刻接会话无意义，
/// 占位提示写入输入框下方的反馈行。
/// </summary>
internal sealed class WelcomePage : Runnable
{
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromSeconds(2);

    private readonly GatewayClient _gateway = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly TextField _input = new();
    private readonly Label _feedback = new();
    private readonly Label _status = new();

    public WelcomePage()
    {
        Title = "TinadecOffice";
        SetScheme(AppTheme.Welcome);

        Label brand = new()
        {
            Text = "TinadecOffice",
            X = Pos.Center(),
            Y = 0,
            Width = Dim.Auto(DimAutoStyle.Text),
        };
        brand.SetScheme(AppTheme.Brand);

        Label subtitle = new()
        {
            Text = "开始一个项目",
            X = Pos.Center(),
            Y = 1,
            Width = Dim.Auto(DimAutoStyle.Text),
        };
        subtitle.SetScheme(AppTheme.Subtitle);

        Label hint = new()
        {
            Text = "告诉智能体你要做什么…",
            X = 2,
            Y = 3,
        };

        _input = new TextField
        {
            X = 2,
            Y = 4,
            Width = Dim.Fill(2),
        };

        Button send = new() { Text = "发送", X = 2, Y = 5 };
        Button open = new() { Text = "打开项目", X = Pos.Right(send) + 2, Y = 5 };
        Button config = new() { Text = "智能体配置", X = Pos.Right(open) + 2, Y = 5 };

        send.Accepted += (_, _) => ShowPlaceholder("发送：会话功能开发中");
        open.Accepted += (_, _) => ShowPlaceholder("打开项目：项目浏览功能开发中");
        config.Accepted += (_, _) => ShowPlaceholder("智能体配置：设置页功能开发中");
        _input.Accepted += (_, _) => ShowPlaceholder("发送：会话功能开发中");

        _feedback = new Label
        {
            Text = string.Empty,
            X = 2,
            Y = 7,
        };

        _status = new Label
        {
            Text = "● 正在连接后端…",
            X = 1,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(1),
        };

        Add(brand, subtitle, hint, _input, send, open, config, _feedback, _status);
    }

    protected override void OnIsRunningChanged(bool newIsRunning)
    {
        base.OnIsRunningChanged(newIsRunning);

        if (newIsRunning)
        {
            _input.SetFocus();
            _ = PollHealthAsync(_cts.Token);
        }
        else
        {
            _cts.Cancel();
        }
    }

    private void ShowPlaceholder(string message)
    {
        _feedback.Text = $"◌ {message}";
    }

    private async Task PollHealthAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var result = await _gateway.CheckHealthAsync(token).ConfigureAwait(false);
            App?.Invoke(() => UpdateHealth(result));

            try
            {
                await Task.Delay(HealthPollInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void UpdateHealth(GatewayClient.HealthResult result)
    {
        if (result.Ok)
        {
            _status.Text = $"● {result.Status} · {result.Name} {result.Version}";
            _status.SetScheme(AppTheme.Healthy);
        }
        else
        {
            _status.Text = $"● 无法连接后端（{_gateway.BaseUrl}）";
            _status.SetScheme(AppTheme.Unhealthy);
        }
    }
}
