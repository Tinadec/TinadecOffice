using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TinadecTools.Runtime.Sandbox.Windows;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class WindowsSandboxSetup
{
    internal const string SetupModeArg = "--sandbox-setup";

    internal static bool IsSetupMode(string[] args)
        => args.Length > 0 && args[0] == SetupModeArg;

    internal static int RunSetup()
    {
        try
        {
            CreateSandboxAccount();
            Console.WriteLine("Sandbox setup completed successfully.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Sandbox setup failed: {ex.Message}");
            return 1;
        }
    }

    internal static void EnsureSetup()
    {
        if (SandboxAccountManager.AccountExists())
            return;

        TriggerUacSetup();
        if (!SandboxAccountManager.AccountExists())
            throw new InvalidOperationException("Sandbox setup did not create a usable sandbox account.");
    }

    private static void CreateSandboxAccount()
    {
        SandboxAccountManager.CreateSandboxAccount();
    }

    internal static void TriggerUacSetup()
    {
        var psi = CreateSelfStartInfo(SetupModeArg, useShellExecute: true);
        psi.Verb = "runas";
        psi.CreateNoWindow = false;

        try
        {
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
            if (proc is not null && proc.ExitCode != 0)
                throw new InvalidOperationException($"Sandbox setup process exited with code {proc.ExitCode}.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException("UAC elevation was cancelled or failed.", ex);
        }
    }

    internal static ProcessStartInfo CreateSelfStartInfo(string modeArg, bool useShellExecute)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Unable to determine the TinadecTools executable path.");
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = useShellExecute
        };

        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var entryAssembly = Environment.GetCommandLineArgs()
                .FirstOrDefault(argument => argument.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(entryAssembly))
                throw new InvalidOperationException("Unable to determine the TinadecTools assembly path.");
            psi.ArgumentList.Add(entryAssembly);
        }

        psi.ArgumentList.Add(modeArg);
        return psi;
    }
}
