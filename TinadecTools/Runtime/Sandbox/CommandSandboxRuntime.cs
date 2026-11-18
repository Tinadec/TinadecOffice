using TinadecTools.Tools.FileRW;
using TinadecTools.Tools.Git;

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
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            // One constructor covers both POSIX platforms; it fails closed per-launch when a
            // specific mechanism (landlock ABI, sandbox-exec) is missing rather than running
            // the command unconstrained.
            try { return new Posix.PosixSandboxBackend(); }
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
        if (timeoutMs < SandboxRequestValidator.MinTimeoutMs)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), "timeout_ms must be >= 1.");
        if (timeoutMs > SandboxRequestValidator.MaxTimeoutMs)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), "timeout_ms must be <= 1800000 (30 minutes).");
    }

    internal static SandboxPermissions BuildPermissions(
        IEnumerable<string>? additionalReadPaths,
        IEnumerable<string>? additionalWritePaths,
        IEnumerable<string>? environmentVariableNames)
    {
        var workingDir = WorkspacePathResolver.WorkspaceRoot;

        var readPaths = WorkspacePathResolver.SourceRoots.Select(root => root.Path)
            .Concat(ToolExecutionContext.Current?.WorkspaceFiles.Select(file => file.Path) ?? [])
            .Append(workingDir).Distinct(SandboxPaths.PathComparer).ToList();
        if (ToolExecutionContext.Current is { } context) readPaths.AddRange(context.ReadRoots);
        if (additionalReadPaths is not null)
        {
            foreach (var p in additionalReadPaths)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(p);
                readPaths.Add(SandboxPaths.NormalizeGrantPath(p));
            }
        }

        var writePaths = WorkspacePathResolver.SourceRoots.Select(root => root.Path)
            .Concat(ToolExecutionContext.Current?.WorkspaceFiles.Select(file => file.Path) ?? [])
            .Append(workingDir).Distinct(SandboxPaths.PathComparer).ToList();
        if (ToolExecutionContext.Current?.StorageRoot is { } publicStorage)
            writePaths.AddRange(new[] { Path.Combine(publicStorage, "config"), Path.Combine(publicStorage, "skills") });
        if (ToolExecutionContext.Current is { ProjectStorageWrite: true, StorageRoot: { } storageRoot })
            writePaths.Add(storageRoot);
        if (additionalWritePaths is not null)
        {
            foreach (var p in additionalWritePaths)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(p);
                var full = SandboxPaths.NormalizeGrantPath(p);
                SandboxPaths.EnsureNotBroadWriteTarget(full);
                if (ToolExecutionContext.Current is { } executionContext)
                    SandboxPaths.EnsureNotOverlappingReadRoots(full, executionContext.ReadRoots);
                if (!WorkspaceStoragePolicy.CanAccess(workingDir, full, true, []))
                    throw new UnauthorizedAccessException("Additional shell write grants cannot access protected scope storage.");
                writePaths.Add(full);
            }
        }

        var envVars = new List<string>();
        if (environmentVariableNames is not null)
        {
            foreach (var name in environmentVariableNames)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(name);
                if (!SandboxEnvironment.IsEnvironmentVariableNameValid(name))
                    throw new ArgumentException($"Invalid environment variable name '{name}'.", nameof(environmentVariableNames));
                envVars.Add(name);
            }
        }

        return new SandboxPermissions
        {
            ReadPaths = readPaths,
            WritePaths = writePaths,
            EnvironmentVariableNames = envVars,
            StorageId = ToolExecutionContext.Current?.StorageId,
            StorageRoot = WorkspaceStoragePolicy.StorageRoot(workingDir),
            StorageWrite = ToolExecutionContext.Current?.ProjectStorageWrite == true,
            ProtectedPaths = (ToolExecutionContext.Current?.ProjectStorageWrite == true
                ? WorkspaceStoragePolicy.ForbiddenRoots(workingDir)
                : WorkspaceStoragePolicy.ProtectedPaths(workingDir)).ToList(),
            ReadExceptions = ToolExecutionContext.Current?.ReadRoots.Where(path => !WorkspaceStoragePolicy.PermanentHostPaths().Any(root => WorkspaceRootSet.IsWithin(root, path))).ToList() ?? [],
            EnvironmentOverrides = GitHookPolicy.ShellOverrides(workingDir),
            WriteDeniedPaths = WorkspaceStoragePolicy.GitWriteDeniedPaths(writePaths).ToList()
        };
    }

    internal static SandboxPermissions MergeWithPolicy(SandboxPermissions additional)
    {
        // Governed calls have frozen grants. A later edit/reset of a legacy policy file must not
        // widen or narrow a running agent's sandbox; standalone use retains its existing behavior.
        if (ToolExecutionContext.Current is not null) return additional;
        var policy = SandboxPolicyStore.Load();
        return new SandboxPermissions
        {
            ReadPaths = [.. policy.ReadPaths.Concat(additional.ReadPaths).Distinct(SandboxPaths.PathComparer)],
            WritePaths = [.. policy.WritePaths.Concat(additional.WritePaths).Distinct(SandboxPaths.PathComparer)],
            EnvironmentVariableNames = [.. policy.EnvironmentVariables.Concat(additional.EnvironmentVariableNames).Distinct(SandboxPaths.PathComparer)],
            ProtectedPaths = additional.ProtectedPaths,
            ReadExceptions = additional.ReadExceptions,
            StorageId = additional.StorageId,
            StorageRoot = additional.StorageRoot,
            StorageWrite = additional.StorageWrite,
            EnvironmentOverrides = additional.EnvironmentOverrides,
            WriteDeniedPaths = additional.WriteDeniedPaths
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
        CancellationToken ct,
        string? argumentString = null)
    {
        SandboxRequestValidator.Validate(executable, arguments, workingDirectory, timeoutMs, argumentString: argumentString);

        if (ToolExecutionContext.Current is { } context)
            timeoutMs = Math.Min(timeoutMs, context.Integer("shell", "max_timeout_ms", 1_800_000, 1, 1_800_000));

        var fullWorkDir = SandboxPaths.ValidateWorkingDirectory(workingDirectory);

        await _backend.EnsureSetupAsync(ct).ConfigureAwait(false);

        var request = new SandboxRunnerRequest
        {
            Executable = executable,
            Arguments = arguments,
            ArgumentString = argumentString,
            WorkingDirectory = fullWorkDir,
            Stdin = stdin,
            TimeoutMs = timeoutMs
        };

        var result = await _backend.ExecuteAsync(request, permissions, persistGrants, ct).ConfigureAwait(false);
        if (ToolExecutionContext.Current is { } executionContext)
        {
            var limit = executionContext.Integer("shell", "max_output_chars", 65_536);
            if (result.Stdout.Length > limit) { result.Stdout = result.Stdout[..limit]; result.StdoutTruncated = true; }
            if (result.Stderr.Length > limit) { result.Stderr = result.Stderr[..limit]; result.StderrTruncated = true; }
        }
        return result;
    }

    internal static async Task<SandboxStreamingProcess> StartStreamingAsync(
        string executable,
        List<string> arguments,
        string workingDirectory,
        int timeoutMs,
        SandboxPermissions permissions,
        CancellationToken ct,
        string? argumentString = null)
    {
        SandboxRequestValidator.Validate(executable, arguments, workingDirectory, timeoutMs, argumentString: argumentString);
        var fullWorkDir = SandboxPaths.ValidateWorkingDirectory(workingDirectory);
        await _backend.EnsureSetupAsync(ct).ConfigureAwait(false);
        return await _backend.StartStreamingAsync(new SandboxRunnerRequest
        {
            Executable = executable,
            Arguments = arguments,
            ArgumentString = argumentString,
            WorkingDirectory = fullWorkDir,
            TimeoutMs = timeoutMs
        }, permissions, ct).ConfigureAwait(false);
    }

    private sealed class RestoreBackend(ISandboxBackend backend) : IDisposable
    {
        public void Dispose() => _backend = backend;
    }
}
