using TinadecTools.Tools.FileRW;

namespace TinadecTools.Runtime.Sandbox;

internal static class CommandSandboxRuntime
{
    private static ISandboxBackend _backend = CreateBackend();

    private static ISandboxBackend CreateBackend()
    {
        if (OperatingSystem.IsWindows())
        {
            try { return new Windows.WindowsSandboxBackend(); }
            catch { return new UnsupportedSandboxBackend(); }
        }
        return new UnsupportedSandboxBackend();
    }

    internal static ISandboxBackend GetBackend() => _backend;

    internal static IDisposable OverrideBackendForTests(ISandboxBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        var previous = _backend;
        _backend = backend;
        return new RestoreBackend(previous);
    }

    internal static void ValidateTimeout(int timeoutMs)
    {
        if (timeoutMs < 1)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), "timeout_ms must be >= 1.");
        if (timeoutMs > 1_800_000)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), "timeout_ms must be <= 1800000 (30 minutes).");
    }

    internal static SandboxPermissions BuildPermissions(
        IEnumerable<string>? additionalReadPaths,
        IEnumerable<string>? additionalWritePaths,
        IEnumerable<string>? environmentVariableNames)
    {
        var workingDir = WorkspacePathResolver.WorkspaceRoot;

        var readPaths = new List<string> { workingDir };
        if (additionalReadPaths is not null)
        {
            foreach (var p in additionalReadPaths)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(p);
                readPaths.Add(SandboxPaths.NormalizeGrantPath(p));
            }
        }

        var writePaths = new List<string> { workingDir };
        if (additionalWritePaths is not null)
        {
            foreach (var p in additionalWritePaths)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(p);
                var full = SandboxPaths.NormalizeGrantPath(p);
                SandboxPaths.EnsureNotBroadWriteTarget(full);
                writePaths.Add(full);
            }
        }

        var envVars = new List<string>();
        if (environmentVariableNames is not null)
        {
            foreach (var name in environmentVariableNames)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(name);
                if (SandboxEnvironment.IsEnvironmentVariableNameValid(name))
                    envVars.Add(name);
            }
        }

        return new SandboxPermissions
        {
            ReadPaths = readPaths,
            WritePaths = writePaths,
            EnvironmentVariableNames = envVars
        };
    }

    internal static SandboxPermissions MergeWithPolicy(SandboxPermissions additional)
    {
        var policy = SandboxPolicyStore.Load();
        return new SandboxPermissions
        {
            ReadPaths = [.. policy.ReadPaths.Concat(additional.ReadPaths).Distinct(StringComparer.OrdinalIgnoreCase)],
            WritePaths = [.. policy.WritePaths.Concat(additional.WritePaths).Distinct(StringComparer.OrdinalIgnoreCase)],
            EnvironmentVariableNames = [.. policy.EnvironmentVariables.Concat(additional.EnvironmentVariableNames).Distinct(StringComparer.OrdinalIgnoreCase)]
        };
    }

    internal static async Task<SandboxRunnerResponse> ExecuteSandboxedAsync(
        string executable,
        List<string> arguments,
        string workingDirectory,
        string? stdin,
        int timeoutMs,
        SandboxPermissions permissions,
        bool persistGrants,
        CancellationToken ct)
    {
        ValidateTimeout(timeoutMs);

        var fullWorkDir = SandboxPaths.ValidateWorkingDirectory(workingDirectory);

        await _backend.EnsureSetupAsync(ct).ConfigureAwait(false);

        var request = new SandboxRunnerRequest
        {
            Executable = executable,
            Arguments = arguments,
            WorkingDirectory = fullWorkDir,
            Stdin = stdin,
            TimeoutMs = timeoutMs
        };

        return await _backend.ExecuteAsync(request, permissions, persistGrants, ct).ConfigureAwait(false);
    }

    private sealed class RestoreBackend(ISandboxBackend backend) : IDisposable
    {
        public void Dispose() => _backend = backend;
    }
}
