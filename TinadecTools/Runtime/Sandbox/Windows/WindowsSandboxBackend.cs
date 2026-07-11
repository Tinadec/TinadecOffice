using System.Diagnostics;
using System.Text.Json;
using TinadecTools.Tools.FileRW;

namespace TinadecTools.Runtime.Sandbox.Windows;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class WindowsSandboxBackend : ISandboxBackend
{
    public bool IsSupported => OperatingSystem.IsWindows();

    public bool IsInitialized => SandboxAccountManager.AccountExists();

    public Task EnsureSetupAsync(CancellationToken ct)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("Windows sandbox is only supported on Windows.");
        if (!IsInitialized)
            WindowsSandboxSetup.EnsureSetup();
        return Task.CompletedTask;
    }

    public async Task<SandboxRunnerResponse> ExecuteAsync(
        SandboxRunnerRequest request,
        SandboxPermissions permissions,
        bool persistGrants,
        CancellationToken ct)
    {
        if (!IsInitialized)
            throw new InvalidOperationException("Sandbox not initialized. Call EnsureSetupAsync first.");

        using var aclManager = new AclManager(SandboxAccountManager.AccountName);

        ApplyAcls(aclManager, permissions);

        request.Environment = SandboxEnvironment.Build(
            new Dictionary<string, string>
            {
                ["Profile"] = SandboxAccountManager.GetSandboxProfileDir(),
                ["Cache"] = SandboxAccountManager.GetSandboxCacheDir()
            },
            permissions.EnvironmentVariableNames);

        try
        {
            return await ExecuteViaRunnerSubprocess(request, ct).ConfigureAwait(false);
        }
        finally
        {
            if (!persistGrants)
                aclManager.RevokeAll();
        }
    }

    public Task ResetAsync(SandboxResetScope scope, CancellationToken ct)
    {
        SandboxPolicyStore.Delete();

        if (scope == SandboxResetScope.Machine)
        {
            try { SandboxAccountManager.DeleteSandboxAccount(); }
            catch { }

            try { DeleteIfExists(SandboxAccountManager.GetSandboxCacheDir()); }
            catch { }

            try { DeleteIfExists(SandboxAccountManager.GetSandboxProfileDir()); }
            catch { }
        }

        return Task.CompletedTask;
    }

    private static void ApplyAcls(AclManager aclManager, SandboxPermissions permissions)
    {
        var workspaceRoot = WorkspacePathResolver.WorkspaceRoot;
        aclManager.GrantWrite(workspaceRoot);

        var sandboxDir = Path.Combine(workspaceRoot, ".tinadec");
        Directory.CreateDirectory(sandboxDir);
        aclManager.DenyAll(sandboxDir);

        foreach (var path in permissions.ReadPaths)
        {
            var full = SandboxPaths.NormalizeGrantPath(path);
            if (!SandboxPaths.IsWithinWorkspace(full))
                aclManager.GrantRead(full);
        }

        foreach (var path in permissions.WritePaths)
        {
            var full = SandboxPaths.NormalizeGrantPath(path);
            SandboxPaths.EnsureNotBroadWriteTarget(full);
            if (!SandboxPaths.IsWithinWorkspace(full))
                aclManager.GrantWrite(full);
        }
    }

    private static async Task<SandboxRunnerResponse> ExecuteViaRunnerSubprocess(
        SandboxRunnerRequest request, CancellationToken ct)
    {
        var psi = WindowsSandboxSetup.CreateSelfStartInfo(WindowsSandboxRunner.RunnerModeArg, useShellExecute: false);
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;
        psi.StandardInputEncoding = System.Text.Encoding.UTF8;
        psi.StandardOutputEncoding = System.Text.Encoding.UTF8;
        psi.UserName = SandboxAccountManager.AccountName;
        psi.Domain = SandboxAccountManager.AccountDomain;
        psi.PasswordInClearText = DpapiCredentialStore.LoadPassword();
        psi.LoadUserProfile = true;

        using var process = new Process { StartInfo = psi };
        process.Start();

        var runnerStderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        // Wait for READY
        var readyLine = await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
        if (readyLine != "READY")
        {
            process.Kill(entireProcessTree: true);
            var stderr = await runnerStderrTask.ConfigureAwait(false);
            return new SandboxRunnerResponse { Success = false, Error = $"Runner init failed: {readyLine}. {stderr}" };
        }

        var json = JsonSerializer.Serialize(request, SandboxJsonContext.Default.SandboxRunnerRequest);
        await process.StandardInput.WriteLineAsync(json).ConfigureAwait(false);
        await process.StandardInput.WriteLineAsync("EXIT").ConfigureAwait(false);
        process.StandardInput.Close();

        string? resultJson;
        try
        {
            resultJson = await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        if (string.IsNullOrEmpty(resultJson))
        {
            var stderr = await runnerStderrTask.ConfigureAwait(false);
            return new SandboxRunnerResponse { Success = false, Error = $"No result from runner. {stderr}" };
        }

        return JsonSerializer.Deserialize(resultJson, SandboxJsonContext.Default.SandboxRunnerResponse)
            ?? new SandboxRunnerResponse { Success = false, Error = "Failed to parse result." };
    }

    private static void DeleteIfExists(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
