using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Installer.Core.Models;
using Installer.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using CoreInstaller = Installer.Core.Services.Installer;
using MewApplication = Aprillz.MewUI.Application;

namespace Tinadec.Installer.GUI;

/// <summary>
/// Main MewUI window: search, browse, install/uninstall with live progress.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly IServiceProvider _services;
    private readonly ManifestService _manifests;
    private readonly IStorageProvider _storage;

    private readonly TextBox _searchBox;
    private readonly ListBox _appList;
    private readonly TextBlock _detailBlock;
    private readonly TextBlock _statusBlock;
    private readonly ProgressBar _progressBar;
    private readonly Button _installButton;
    private readonly Button _uninstallButton;

    private List<global::Installer.Core.Models.Application> _visibleApps = new();

    public MainWindow(IServiceProvider services)
    {
        _services = services;
        _manifests = services.GetRequiredService<ManifestService>();
        _storage = services.GetRequiredService<IStorageProvider>();

        Title = "Tinadec Installer — 应用安装与管理器";
        WindowSize = WindowSize.Fixed(980, 660);

        // Header
        var titleBlock = new TextBlock
        {
            Text = "Tinadec 应用商店",
            FontSize = 20,
            Margin = new Thickness(4, 0, 0, 8)
        };

        // Search row
        _searchBox = new TextBox
        {
            Width = 360,
            VerticalAlignment = VerticalAlignment.Center
        };
        var searchButton = new Button
        {
            Content = new TextBlock { Text = "搜索" }
        };
        searchButton.Click += RefreshList;

        var searchPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 8)
        };
        searchPanel.Add(_searchBox);
        searchPanel.Add(searchButton);

        // Application list
        var listCaption = new TextBlock { Text = "应用列表", Margin = new Thickness(4, 0, 0, 4) };
        _appList = new ListBox
        {
            Height = 240,
            ItemsSource = ItemsView.Create<string>([])
        };
        _appList.SelectionChanged += OnSelectionChanged;

        // Detail pane
        var detailCaption = new TextBlock { Text = "详情", Margin = new Thickness(4, 8, 0, 4) };
        _detailBlock = new TextBlock
        {
            Text = "选择一个应用查看详情",
            TextWrapping = TextWrapping.Wrap,
            Height = 96
        };

        // Progress + status
        _progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 10,
            Margin = new Thickness(0, 8, 0, 4)
        };
        _statusBlock = new TextBlock { Text = "就绪" };

        // Action buttons
        _installButton = new Button { Content = new TextBlock { Text = "安装 / 升级" } };
        _installButton.Click += RunInstall;

        _uninstallButton = new Button { Content = new TextBlock { Text = "卸载" } };
        _uninstallButton.Click += RunUninstall;

        var refreshButton = new Button { Content = new TextBlock { Text = "刷新" } };
        refreshButton.Click += RefreshList;

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 10, 0, 0)
        };
        buttonPanel.Add(_installButton);
        buttonPanel.Add(_uninstallButton);
        buttonPanel.Add(refreshButton);

        var root = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(16)
        };
        root.Add(titleBlock);
        root.Add(searchPanel);
        root.Add(listCaption);
        root.Add(_appList);
        root.Add(detailCaption);
        root.Add(_detailBlock);
        root.Add(_progressBar);
        root.Add(_statusBlock);
        root.Add(buttonPanel);

        Content = root;

        RefreshList();
    }

    // ----- data binding -----

    private void RefreshList()
    {
        var keyword = _searchBox.Text;
        _visibleApps = string.IsNullOrWhiteSpace(keyword)
            ? _manifests.GetAllApplications().ToList()
            : _manifests.Search(keyword);

        var rows = _visibleApps
            .Select(a => $"{a.Id,-20} v{a.Version,-10} {a.Name}")
            .ToList();

        _appList.ItemsSource = ItemsView.Create(rows);
        _statusBlock.Text = $"已加载 {_visibleApps.Count} 个应用";
        _progressBar.Value = 0;
    }

    private global::Installer.Core.Models.Application? SelectedApp
    {
        get
        {
            var index = _appList.SelectedIndex;
            return index >= 0 && index < _visibleApps.Count ? _visibleApps[index] : null;
        }
    }

    private void OnSelectionChanged(object? selectedItem)
    {
        var app = SelectedApp;
        if (app is null)
        {
            _detailBlock.Text = "选择一个应用查看详情";
            return;
        }

        var installed = _storage.LoadAsync<InstalledApplication>(app.Id).GetAwaiter().GetResult();
        var state = installed is { IsActive: true }
            ? $"已安装 v{installed.Version}\n路径: {installed.InstallPath}"
            : "未安装";

        _detailBlock.Text =
            $"{app.Name} ({app.Id}) — {state}\n" +
            $"{app.Description}\n" +
            $"仓库: {app.GitHubRepo}   标签: {string.Join(", ", app.Tags)}";
    }

    // ----- actions -----

    private void RunInstall()
    {
        var app = SelectedApp;
        if (app is null)
        {
            MessageBox.Notify("请先在列表中选择一个应用。", PromptIconKind.Info, "提示", this);
            return;
        }

        SetBusy(true);
        _statusBlock.Text = $"正在安装 {app.Name} ...";
        _progressBar.Value = 0;

        Task.Run(async () =>
        {
            try
            {
                using var scope = _services.CreateScope();
                var installer = scope.ServiceProvider.GetRequiredService<CoreInstaller>();
                var result = await installer.InstallAsync(app, null, progress =>
                    InvokeOnUi(() =>
                    {
                        _progressBar.Value = progress.Percentage;
                        _statusBlock.Text = $"{progress.Phase}: {progress.Message}";
                    }));

                InvokeOnUi(() =>
                {
                    SetBusy(false);
                    _progressBar.Value = 100;
                    _statusBlock.Text = $"已安装 {result.Name} v{result.Version}";
                    MessageBox.Notify(
                        $"已安装 {result.Name} v{result.Version}\n{result.InstallPath}",
                        PromptIconKind.Success, "安装完成", this);
                    RefreshList();
                });
            }
            catch (Exception ex)
            {
                InvokeOnUi(() =>
                {
                    SetBusy(false);
                    _statusBlock.Text = $"安装失败: {ex.Message}";
                    MessageBox.Notify(ex.Message, PromptIconKind.Error, "安装失败", this);
                });
            }
        });
    }

    private void RunUninstall()
    {
        var app = SelectedApp;
        if (app is null)
        {
            MessageBox.Notify("请先在列表中选择一个应用。", PromptIconKind.Info, "提示", this);
            return;
        }

        var installed = _storage.LoadAsync<InstalledApplication>(app.Id).GetAwaiter().GetResult();
        if (installed is null)
        {
            MessageBox.Notify($"'{app.Id}' 尚未安装。", PromptIconKind.Info, "提示", this);
            return;
        }

        var confirmed = MessageBox.AskYesNo(
            $"卸载 {installed.Name} v{installed.Version}？\n（配置将被保留）",
            PromptIconKind.Question, "确认卸载", this);

        if (!confirmed)
        {
            return;
        }

        SetBusy(true);
        _statusBlock.Text = $"正在卸载 {installed.Name} ...";

        Task.Run(async () =>
        {
            try
            {
                using var scope = _services.CreateScope();
                var installer = scope.ServiceProvider.GetRequiredService<CoreInstaller>();
                await installer.UninstallAsync(app.Id, preserveConfig: true, progress =>
                    InvokeOnUi(() => _statusBlock.Text = $"{progress.Phase}: {progress.Message}"));

                InvokeOnUi(() =>
                {
                    SetBusy(false);
                    _statusBlock.Text = $"已卸载 {app.Id}（配置已保留）";
                    RefreshList();
                });
            }
            catch (Exception ex)
            {
                InvokeOnUi(() =>
                {
                    SetBusy(false);
                    _statusBlock.Text = $"卸载失败: {ex.Message}";
                    MessageBox.Notify(ex.Message, PromptIconKind.Error, "卸载失败", this);
                });
            }
        });
    }

    private void SetBusy(bool busy)
    {
        _installButton.IsEnabled = !busy;
        _uninstallButton.IsEnabled = !busy;
    }

    private static void InvokeOnUi(Action action)
    {
        var dispatcher = MewApplication.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.IsOnUIThread)
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }
}
