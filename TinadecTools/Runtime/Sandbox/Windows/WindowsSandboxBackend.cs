using System.Diagnostics;
using System.Text.Json;
using TinadecTools.Tools.FileRW;

namespace TinadecTools.Runtime.Sandbox.Windows;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class WindowsSandboxBackend : ISandboxBackend
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> SetupGates = new();
    public bool IsSupported => OperatingSystem.IsWindows();

    public bool IsInitialized => SandboxAccountManager.AccountExists();

    public async Task EnsureSetupAsync(CancellationToken ct)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("Windows sandbox is only supported on Windows.");
        var gate = SetupGates.GetOrAdd(SandboxAccountManager.AccountName, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { if (!IsInitialized) await WindowsSandboxSetup.EnsureSetupAsync(ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<SandboxRunnerResponse> ExecuteAsync(
        SandboxRunnerRequest request,
        SandboxPermissions permissions,
        bool persistGrants,
        CancellationToken ct)
    {
        if (!IsInitialized)
            throw new InvalidOperationException("Sandbox not initialized. Call EnsureSetupAsync first.");

        var aclManager = new AclManager(SandboxAccountManager.AccountName);

        ApplyAcls(aclManager, permissions);

        request.Environment = SandboxEnvironment.Build(
            new Dictionary<string, string>
            {
                ["Profile"] = SandboxAccountManager.GetSandboxProfileDir(),
                ["Cache"] = SandboxAccountManager.GetSandboxCacheDir()
            },
            permissions.EnvironmentVariableNames);
        foreach (var entry in permissions.EnvironmentOverrides) request.Environment[entry.Key] = entry.Value;
        request.Environment.Remove("TINADEC_HOST_CONTROL_TOKEN");

        try
        {
            return await ExecuteViaRunnerSubprocess(request, ct).ConfigureAwait(false);
        }
        finally
        {
            if (!persistGrants)
                aclManager.RevokeAllAfter(TimeSpan.FromSeconds(30));
        }
    }

    public Task<SandboxStreamingProcess> StartStreamingAsync(
        SandboxRunnerRequest request,
        SandboxPermissions permissions,
        CancellationToken ct)
    {
        if (!IsInitialized)
            throw new InvalidOperationException("Sandbox not initialized. Call EnsureSetupAsync first.");
        ct.ThrowIfCancellationRequested();

        var aclManager = new AclManager(SandboxAccountManager.AccountName);
        JobObjectManager? job = null;
        try
        {
            ApplyAcls(aclManager, permissions);
            request.Environment = SandboxEnvironment.Build(
                new Dictionary<string, string>
                {
                    ["Profile"] = SandboxAccountManager.GetSandboxProfileDir(),
                    ["Cache"] = SandboxAccountManager.GetSandboxCacheDir()
                },
                permissions.EnvironmentVariableNames);
            foreach (var entry in permissions.EnvironmentOverrides) request.Environment[entry.Key] = entry.Value;
            request.Environment.Remove("TINADEC_HOST_CONTROL_TOKEN");

            var psi = new ProcessStartInfo(request.Executable)
            {
                WorkingDirectory = request.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                UserName = SandboxAccountManager.AccountName,
                Domain = SandboxAccountManager.AccountDomain,
                PasswordInClearText = DpapiCredentialStore.LoadPassword(),
                // The sandbox environment sets USERPROFILE/APPDATA/TEMP explicitly.
                // Loading the local profile on every command costs tens of seconds on
                // Windows and is not part of the security boundary.
                LoadUserProfile = false,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            request.ApplyCommandLine(psi);
            psi.Environment.Clear();
            foreach (var pair in request.Environment) psi.Environment[pair.Key] = pair.Value;

            var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start sandboxed streaming process.");
            job = new JobObjectManager();
            job.Assign(process.Handle);
            return Task.FromResult(new SandboxStreamingProcess(process, new StreamingCleanup(aclManager, job)));
        }
        catch
        {
            job?.Dispose();
            aclManager.Dispose();
            throw;
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
        foreach (var path in permissions.ReadPaths)
        {
            var full = SandboxPaths.NormalizeGrantPath(path);
            if (Directory.Exists(full) || File.Exists(full)) aclManager.GrantRead(full);
        }

        foreach (var path in permissions.WritePaths)
        {
            var full = SandboxPaths.NormalizeGrantPath(path);
            SandboxPaths.EnsureNotBroadWriteTarget(full);
            if (Directory.Exists(full) || File.Exists(full)) aclManager.GrantWrite(full);
        }
        foreach (var path in permissions.ProtectedPaths)
            if (Directory.Exists(path) || File.Exists(path)) aclManager.DenyAll(path);
        if (!permissions.StorageWrite && permissions.StorageRoot is { } root && Directory.Exists(root))
        {
            aclManager.DenyWrite(root);
            foreach (var name in new[] { "config", "skills" })
            {
                var source = Path.Combine(root, name);
                if (Directory.Exists(source) && permissions.WritePaths.Any(grant => WorkspaceRootSet.IsWithin(grant, source)))
                    aclManager.AllowWriteException(source);
            }
        }
        foreach (var path in permissions.ReadExceptions)
            if (Directory.Exists(path)) aclManager.AllowReadException(path);
        foreach (var path in permissions.WritePaths)
            if (Directory.Exists(path) && permissions.ProtectedPaths.Any(root => root != path && WorkspaceRootSet.IsWithin(root, path)))
                aclManager.AllowWriteException(path);

        var cache = SandboxAccountManager.GetSandboxCacheDir();
        Directory.CreateDirectory(cache);
        aclManager.AllowWriteException(cache);
        if (ToolExecutionContext.Current?.StorageRoot is { } storage)
        {
            var temporary = Path.Combine(storage, "temp", "sandbox", SandboxAccountManager.AccountName);
            Directory.CreateDirectory(temporary);
            aclManager.AllowWriteException(temporary);
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
        // Folder grants travel through the private host spawn channel, never
        // through the model-authored command request.
        psi.WorkingDirectory = WorkspacePathResolver.WorkspaceRoot;
        foreach (var source in WorkspacePathResolver.SourceRoots)
        {
            psi.ArgumentList.Add("--workspace-root");
            psi.ArgumentList.Add(source.Path);
        }
        psi.StandardInputEncoding = System.Text.Encoding.UTF8;
        psi.StandardOutputEncoding = System.Text.Encoding.UTF8;
        psi.UserName = SandboxAccountManager.AccountName;
        psi.Domain = SandboxAccountManager.AccountDomain;
        psi.PasswordInClearText = DpapiCredentialStore.LoadPassword();
        psi.LoadUserProfile = false;

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

    private sealed class StreamingCleanup(AclManager aclManager, JobObjectManager job) : IDisposable
    {
        public void Dispose()
        {
            job.Dispose();
            aclManager.Dispose();
        }
    }
}
