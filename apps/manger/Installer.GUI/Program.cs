using Aprillz.MewUI;
using Installer.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using MewApplication = Aprillz.MewUI.Application;

namespace Tinadec.Installer.GUI;

/// <summary>
/// GUI entry point using MewUI (NativeAOT-friendly, code-first).
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var services = new ServiceCollection();
        services.AddInstallerCore(options => options.MinFreeSpaceMb = 256);
        var provider = services.BuildServiceProvider();

        var builder = MewApplication.Create();
        builder.BuildMainWindow(() => new MainWindow(provider));
        builder.Run();

        provider.Dispose();
        return 0;
    }
}
