using Installer.Core.Models;
using Installer.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Terminal.Gui;
using Application = Terminal.Gui.Application;
using CoreInstaller = Installer.Core.Services.Installer;

namespace Tinadec.Installer.TUI;

/// <summary>
/// Interactive Terminal.Gui dashboard: search, install, uninstall, upgrade
/// with live progress reporting. Launched when the installer runs without args.
/// </summary>
internal sealed class DashboardUi
{
    private readonly IServiceProvider _services;
    private readonly ManifestService _manifests;
    private readonly IStorageProvider _storage;

    private Window? _window;
    private TextField? _searchField;
    private ListView? _appList;
    private Label? _detailLabel;
    private Label? _statusLabel;
    private ProgressBar? _progressBar;
    private Button? _installButton;
    private Button? _uninstallButton;

    private List<global::Installer.Core.Models.Application> _visibleApps = new();
    private global::Installer.Core.Models.Application? _selectedApp;

    private DashboardUi(IServiceProvider services)
    {
        _services = services;
        _manifests = services.GetRequiredService<ManifestService>();
        _storage = services.GetRequiredService<IStorageProvider>();
    }

    public static int Run(IServiceProvider services)
    {
        Application.Init();

        try
        {
            var ui = new DashboardUi(services);
            var window = ui.Build();
            Application.Run(window);
            window.Dispose();
            return 0;
        }
        finally
        {
            Application.Shutdown();
        }
    }

    private Window Build()
    {
        _window = new Window
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Title = "Tinadec Installer — 应用安装与管理器 (ESC/Q 退出)"
        };

        // Search row
        var searchCaption = new Label
        {
            X = 1,
            Y = 1,
            Width = 10,
            Text = "搜索:"
        };
        _searchField = new TextField
        {
            X = Pos.Right(searchCaption) + 1,
            Y = 1,
            Width = 40
        };
        _searchField.TextChanged += (_, _) => RefreshList();

        var refreshButton = new Button
        {
            X = Pos.Right(_searchField) + 2,
            Y = 1,
            Text = "刷新"
        };
        refreshButton.Accepting += (_, _) => RefreshList();

        // Application list
        var listCaption = new Label
        {
            X = 1,
            Y = 3,
            Width = Dim.Fill(),
            Text = "应用列表 (回车/双击查看详情)"
        };
        _appList = new ListView
        {
            X = 1,
            Y = Pos.Bottom(listCaption),
            Width = Dim.Fill(2),
            Height = 10,
            AllowsMultipleSelection = false
        };
        _appList.SelectedItemChanged += (sender, args) => OnSelectionChanged(args.Item);
        _appList.OpenSelectedItem += (sender, args) => RunInstall();

        // Detail pane
        var detailCaption = new Label
        {
            X = 1,
            Y = Pos.Bottom(_appList) + 1,
            Width = Dim.Fill(),
            Text = "详情"
        };
        _detailLabel = new Label
        {
            X = 1,
            Y = Pos.Bottom(detailCaption),
            Width = Dim.Fill(2),
            Height = 6,
            Text = "选择一个应用查看详情"
        };

        // Progress + status
        _progressBar = new ProgressBar
        {
            X = 1,
            Y = Pos.Bottom(_detailLabel) + 1,
            Width = Dim.Fill(2),
            Height = 1,
            Fraction = 0
        };
        _statusLabel = new Label
        {
            X = 1,
            Y = Pos.Bottom(_progressBar) + 1,
            Width = Dim.Fill(2),
            Text = "就绪"
        };

        // Action buttons
        _installButton = new Button
        {
            X = 1,
            Y = Pos.Bottom(_statusLabel) + 1,
            Text = "安装/升级"
        };
        _installButton.Accepting += (sender, args) => RunInstall();

        _uninstallButton = new Button
        {
            X = Pos.Right(_installButton) + 2,
            Y = Pos.Bottom(_statusLabel) + 1,
            Text = "卸载"
        };
        _uninstallButton.Accepting += (sender, args) => RunUninstall();

        var quitButton = new Button
        {
            X = Pos.Right(_uninstallButton) + 2,
            Y = Pos.Bottom(_statusLabel) + 1,
            Text = "退出"
        };
        quitButton.Accepting += (sender, args) => Application.RequestStop();

        _window.Add(searchCaption, _searchField, refreshButton,
            listCaption, _appList,
            detailCaption, _detailLabel,
            _progressBar, _statusLabel,
            _installButton, _uninstallButton, quitButton);

        RefreshList();
        return _window;
    }

    // ----- data binding -----

    private void RefreshList()
    {
        var keyword = _searchField?.Text?.ToString() ?? "";
        _visibleApps = string.IsNullOrWhiteSpace(keyword)
            ? _manifests.GetAllApplications().ToList()
            : _manifests.Search(keyword);

        var rows = new System.Collections.ObjectModel.ObservableCollection<string>();
        foreach (var app in _visibleApps)
        {
            rows.Add($"{app.Id,-18} v{app.Version,-10} {app.Name}");
        }

        _appList!.Source = new ListWrapper<string>(rows);
        SetStatus($"已加载 {_visibleApps.Count} 个应用");
    }

    private void OnSelectionChanged(int index)
    {
        if (index < 0 || index >= _visibleApps.Count)
        {
            _selectedApp = null;
            return;
        }

        _selectedApp = _visibleApps[index];
        var app = _selectedApp;

        var installed = _storage.LoadAsync<InstalledApplication>(app.Id).GetAwaiter().GetResult();
        var state = installed is { IsActive: true }
            ? $"已安装 v{installed.Version} @ {installed.InstallPath}"
            : "未安装";

        _detailLabel!.Text =
            $"{app.Name} ({app.Id}) — {state}\n" +
            $"  {app.Description}\n" +
            $"  仓库: {app.GitHubRepo}   标签: {string.Join(", ", app.Tags)}";
    }

    // ----- actions -----

    private void RunInstall()
    {
        if (_selectedApp is null)
        {
            SetStatus("请先在列表中选择应用");
            return;
        }

        var app = _selectedApp;
        SetBusy(true);
        SetStatus($"正在安装 {app.Name} ...");

        Task.Run(async () =>
        {
            try
            {
                using var scope = _services.CreateScope();
                var installer = scope.ServiceProvider.GetRequiredService<CoreInstaller>();
                var result = await installer.InstallAsync(app, null, progress =>
                    Application.Invoke(() =>
                    {
                        _progressBar!.Fraction = (float)(progress.Percentage / 100.0);
                        SetStatus($"{progress.Phase}: {progress.Message}");
                    }));

                Application.Invoke(() =>
                {
                    SetBusy(false);
                    _progressBar!.Fraction = 1;
                    SetStatus($"已安装 {result.Name} v{result.Version}");
                    RefreshList();
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() =>
                {
                    SetBusy(false);
                    SetStatus($"安装失败: {ex.Message}");
                });
            }
        });
    }

    private void RunUninstall()
    {
        if (_selectedApp is null)
        {
            SetStatus("请先在列表中选择应用");
            return;
        }

        var app = _selectedApp;
        var installed = _storage.LoadAsync<InstalledApplication>(app.Id).GetAwaiter().GetResult();
        if (installed is null)
        {
            SetStatus($"{app.Id} 未安装");
            return;
        }

        var confirmed = MessageBox.Query(
            "确认卸载",
            $"卸载 {installed.Name} v{installed.Version}？",
            "卸载", "取消");

        if (confirmed != 0)
        {
            return;
        }

        SetBusy(true);
        SetStatus($"正在卸载 {installed.Name} ...");

        Task.Run(async () =>
        {
            try
            {
                using var scope = _services.CreateScope();
                var installer = scope.ServiceProvider.GetRequiredService<CoreInstaller>();
                await installer.UninstallAsync(app.Id, preserveConfig: true, progress =>
                    Application.Invoke(() => SetStatus($"{progress.Phase}: {progress.Message}")));

                Application.Invoke(() =>
                {
                    SetBusy(false);
                    SetStatus($"已卸载 {app.Id}（配置已保留）");
                    RefreshList();
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() =>
                {
                    SetBusy(false);
                    SetStatus($"卸载失败: {ex.Message}");
                });
            }
        });
    }

    private void SetBusy(bool busy)
    {
        if (_installButton is not null) _installButton.Enabled = !busy;
        if (_uninstallButton is not null) _uninstallButton.Enabled = !busy;
    }

    private void SetStatus(string message)
    {
        if (_statusLabel is not null)
        {
            _statusLabel.Text = message;
        }
    }
}
