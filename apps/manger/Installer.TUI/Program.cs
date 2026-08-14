using Installer.Core.Helpers;
using Installer.Core.Models;
using Installer.Core.Services;
using CoreInstaller = Installer.Core.Services.Installer;
using Microsoft.Extensions.DependencyInjection;

namespace Tinadec.Installer.TUI;

/// <summary>
/// TUI entry point: CLI command dispatch + optional Terminal.Gui dashboard.
///
/// Usage:
///   tinadec-installer                    -> interactive Terminal.Gui dashboard
///   tinadec-installer install <id> [--version <v>]
///   tinadec-installer uninstall <id> [--keep-config] [--yes]
///   tinadec-installer list [--available]
///   tinadec-installer search <keyword>
///   tinadec-installer upgrade <id> | upgrade --all
///   tinadec-installer info <id>
/// </summary>
public static class Program
{
    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddInstallerCore(options => options.MinFreeSpaceMb = 256);
        return services.BuildServiceProvider();
    }

    public static async Task<int> Main(string[] args)
    {
        PathHelper.EnsureDirectories();

        if (args.Length == 0)
        {
            return RunDashboard();
        }

        var command = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        using var sp = BuildServices();
        try
        {
            return command switch
            {
                "install" => await InstallCommand(sp, rest),
                "uninstall" => await UninstallCommand(sp, rest),
                "list" or "ls" => await ListCommand(sp, rest),
                "search" => await SearchCommand(sp, rest),
                "upgrade" or "update" => await UpgradeCommand(sp, rest),
                "info" => await InfoCommand(sp, rest),
                "help" or "--help" or "-h" => HelpCommand(),
                _ => UnknownCommand(command)
            };
        }
        catch (InstallationException ex)
        {
            WriteError(ex.Category switch
            {
                ErrorCategory.Network => $"[网络] {ex.Message}",
                ErrorCategory.DiskSpace => $"[磁盘] {ex.Message}",
                ErrorCategory.Validation => $"[验证] {ex.Message}",
                ErrorCategory.Corruption => $"[损坏] {ex.Message}",
                _ => ex.Message
            });
            return 1;
        }
        catch (GitHubApiException ex)
        {
            WriteError($"[GitHub] {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            WriteError($"发生意外错误: {ex.Message}");
            return 1;
        }
    }

    // ----- commands -----

    private static async Task<int> InstallCommand(ServiceProvider sp, string[] args)
    {
        if (args.Length == 0) { WriteError("用法: install <app-id> [--version <v>]"); return 1; }

        var appId = args[0];
        string? version = null;
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (args[i] is "--version" or "-v") version = args[i + 1];
        }

        var manifests = sp.GetRequiredService<ManifestService>();
        var app = manifests.FindByIdOrName(appId);
        if (app is null)
        {
            WriteError($"未找到应用 '{appId}'。使用 search 查看可用应用。");
            return 1;
        }

        var storage = sp.GetRequiredService<IStorageProvider>();
        var existing = await storage.LoadAsync<InstalledApplication>(app.Id);

        if (existing is { IsActive: true })
        {
            Console.WriteLine($"'{app.Id}' 已安装 v{existing.Version}。");
            if (version is null || version == existing.Version)
            {
                Console.WriteLine("如需升级请使用: upgrade " + app.Id);
                return 0;
            }
        }

        Console.WriteLine($"正在安装 {app.Name} {version ?? "(最新)"} ...");

        var installer = sp.GetRequiredService<CoreInstaller>();
        var result = await installer.InstallAsync(app, version, progress => RenderProgress(progress));

        Console.WriteLine();
        WriteSuccess($"已安装 {result.Name} v{result.Version}");
        Console.WriteLine($"  路径: {result.InstallPath}");
        Console.WriteLine($"  校验: sha256:{result.Checksum?[..16]}...");
        return 0;
    }

    private static async Task<int> UninstallCommand(ServiceProvider sp, string[] args)
    {
        if (args.Length == 0) { WriteError("用法: uninstall <app-id> [--keep-config] [--yes]"); return 1; }

        var appId = args[0];
        var keepConfig = args.Contains("--keep-config");
        var assumeYes = args.Contains("--yes") || args.Contains("-y");

        var storage = sp.GetRequiredService<IStorageProvider>();
        var existing = await storage.LoadAsync<InstalledApplication>(appId);
        if (existing is null)
        {
            WriteError($"'{appId}' 未安装。");
            return 1;
        }

        if (!assumeYes)
        {
            Console.Write($"确认卸载 {existing.Name} v{existing.Version}? (y/N) ");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (answer is not ("y" or "yes")) { Console.WriteLine("已取消。"); return 0; }
        }

        var installer = sp.GetRequiredService<CoreInstaller>();
        await installer.UninstallAsync(appId, keepConfig, progress =>
            Console.WriteLine($"  {progress.Phase}: {progress.Message}"));

        WriteSuccess($"已卸载 {existing.Name}" + (keepConfig ? "（配置已保留）" : ""));
        return 0;
    }

    private static async Task<int> ListCommand(ServiceProvider sp, string[] args)
    {
        var showAvailable = args.Contains("--available") || args.Contains("-a");

        var storage = sp.GetRequiredService<IStorageProvider>();
        var manifests = sp.GetRequiredService<ManifestService>();

        if (showAvailable)
        {
            Console.WriteLine("可用应用（注册清单）:");
            foreach (var app in manifests.GetAllApplications())
            {
                Console.WriteLine($"  {app.Id,-20} {app.Version,-10} {app.Name}");
                Console.WriteLine($"  {"",-20} {app.Description}");
            }
            return 0;
        }

        var installed = await storage.ListInstalledAsync();
        if (installed.Count == 0)
        {
            Console.WriteLine("尚未安装任何应用。使用 'list --available' 查看可安装的应用。");
            return 0;
        }

        Console.WriteLine($"已安装应用 ({installed.Count}):");
        foreach (var app in installed)
        {
            Console.WriteLine($"  {app.Id,-20} v{app.Version,-10} {app.InstalledAt:yyyy-MM-dd}");
            Console.WriteLine($"  {"",-20} {app.InstallPath}");
        }
        return 0;
    }

    private static async Task<int> SearchCommand(ServiceProvider sp, string[] args)
    {
        if (args.Length == 0) { WriteError("用法: search <keyword>"); return 1; }

        var manifests = sp.GetRequiredService<ManifestService>();
        var results = manifests.Search(args[0]);

        if (results.Count == 0)
        {
            Console.WriteLine($"没有匹配 '{args[0]}' 的应用。");
            return 0;
        }

        Console.WriteLine($"搜索结果 ({results.Count}):");
        foreach (var app in results)
        {
            Console.WriteLine($"  {app.Id,-20} v{app.Version,-10} {app.Name}");
            Console.WriteLine($"  {"",-20} 标签: {string.Join(", ", app.Tags)}");
        }
        return 0;
    }

    private static async Task<int> UpgradeCommand(ServiceProvider sp, string[] args)
    {
        var upgradeAll = args.Contains("--all") || args.Contains("-a");
        var dryRun = args.Contains("--dry-run");
        var appId = args.FirstOrDefault(a => !a.StartsWith('-'));

        var versionManager = sp.GetRequiredService<VersionManager>();
        var manifests = sp.GetRequiredService<ManifestService>();
        var installer = sp.GetRequiredService<CoreInstaller>();

        List<(InstalledApplication Installed, string Available)> outdated;

        if (upgradeAll)
        {
            outdated = await versionManager.DetectOutdatedAsync(manifests);
        }
        else
        {
            if (appId is null) { WriteError("用法: upgrade <app-id> 或 upgrade --all"); return 1; }
            var existing = await sp.GetRequiredService<IStorageProvider>().LoadAsync<InstalledApplication>(appId);
            if (existing is null) { WriteError($"'{appId}' 未安装。"); return 1; }

            var manifest = manifests.FindByIdOrName(appId);
            if (manifest is null || manifest.Version == existing.Version)
            {
                Console.WriteLine($"'{appId}' 已是最新版本 ({existing.Version})。");
                return 0;
            }
            outdated = [(existing, manifest.Version)];
        }

        if (outdated.Count == 0)
        {
            Console.WriteLine("所有应用均为最新版本。");
            return 0;
        }

        Console.WriteLine("可升级应用:");
        foreach (var (installed, available) in outdated)
        {
            Console.WriteLine($"  {installed.Id}: {installed.Version} -> {available}");
        }

        if (dryRun)
        {
            Console.WriteLine("(dry-run 模式，未执行升级)");
            return 0;
        }

        var failures = 0;
        foreach (var (installed, _) in outdated)
        {
            try
            {
                Console.WriteLine($"正在升级 {installed.Id} ...");
                var result = await installer.UpgradeAsync(installed.Id, null, RenderProgress);
                WriteSuccess($"已升级 {result.Id} -> v{result.Version}");
            }
            catch (Exception ex)
            {
                failures++;
                WriteError($"升级 {installed.Id} 失败: {ex.Message}");
            }
        }

        return failures == 0 ? 0 : 1;
    }

    private static async Task<int> InfoCommand(ServiceProvider sp, string[] args)
    {
        if (args.Length == 0) { WriteError("用法: info <app-id>"); return 1; }

        var manifests = sp.GetRequiredService<ManifestService>();
        var storage = sp.GetRequiredService<IStorageProvider>();
        var app = manifests.FindByIdOrName(args[0]);

        if (app is null)
        {
            WriteError($"未找到应用 '{args[0]}'。");
            return 1;
        }

        Console.WriteLine($"应用:   {app.Name} ({app.Id})");
        Console.WriteLine($"描述:   {app.Description}");
        Console.WriteLine($"仓库:   {app.GitHubRepo}");
        Console.WriteLine($"最新版: {app.Version}");
        Console.WriteLine($"标签:   {string.Join(", ", app.Tags)}");

        if (app.Dependencies.Count > 0)
        {
            Console.WriteLine("依赖:");
            foreach (var dep in app.Dependencies)
            {
                Console.WriteLine($"  - {dep.PackageId} {dep.VersionRange}{(dep.Optional ? " (可选)" : "")}");
            }
        }

        var installed = await storage.LoadAsync<InstalledApplication>(app.Id);
        if (installed is { IsActive: true })
        {
            Console.WriteLine();
            Console.WriteLine($"已安装: v{installed.Version}");
            Console.WriteLine($"路径:   {installed.InstallPath}");
            Console.WriteLine($"时间:   {installed.InstalledAt:yyyy-MM-dd HH:mm}");
            if (installed.Checksum is { Length: > 16 })
                Console.WriteLine($"校验:   sha256:{installed.Checksum[..16]}...");
        }
        return 0;
    }

    private static int HelpCommand()
    {
        Console.WriteLine("""
            Tinadec Installer (TUI) - 应用安装与管理器

            用法: tinadec-installer [命令] [参数]

            命令:
              (无命令)                 启动 Terminal.Gui 交互式仪表盘
              install <id> [--version] 安装应用 (默认最新版)
              uninstall <id> [选项]    卸载应用 (--keep-config 保留配置, --yes 跳过确认)
              list [--available]       列出已安装/可安装应用
              search <keyword>         按关键词搜索应用
              upgrade <id> | --all     升级应用 (--dry-run 仅预览)
              info <id>                查看应用详情
              help                     显示此帮助
            """);
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        WriteError($"未知命令 '{command}'。使用 help 查看用法。");
        return 1;
    }

    private static int RunDashboard()
    {
        try
        {
            return DashboardUi.Run(BuildServices());
        }
        catch (Exception ex)
        {
            WriteError($"仪表盘启动失败: {ex.Message}（可使用 CLI 命令模式，help 查看用法）");
            return 1;
        }
    }

    // ----- output helpers -----

    private static void RenderProgress(InstallProgress progress)
    {
        var width = 32;
        var filled = (int)Math.Clamp(progress.Percentage / 100.0 * width, 0, width);
        var bar = new string('=', filled) + new string(' ', width - filled);
        var sizeInfo = progress.TotalBytes > 0
            ? $" {FormatBytes(progress.DownloadedBytes)}/{FormatBytes(progress.TotalBytes)}"
            : "";
        Console.Write($"\r  [{bar}] {progress.Percentage,3:F0}% {progress.Phase}{sizeInfo}   ");
        if (progress.Percentage >= 100) Console.WriteLine();
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024.0:F0}KB",
        _ => $"{bytes / 1024.0 / 1024:F1}MB"
    };

    private static void WriteSuccess(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    private static void WriteError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ResetColor();
    }
}
