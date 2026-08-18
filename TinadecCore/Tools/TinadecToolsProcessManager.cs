using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Tools;

/// <summary>
/// Manages one TinadecTools child process per workspace root, speaking the
/// line-delimited JSON protocol over redirected stdio. Calls are correlated by
/// an incrementing call id; a single write lock serializes request lines; a
/// per-process read loop dispatches responses. Crashed processes fail their
/// in-flight calls with a structured process_exit result and are restarted on
/// the next call.
/// </summary>
public sealed class TinadecToolsProcessManager : IToolProcessManager, IHostedService, IDisposable
{
    private const string ManifestToolId = "#manifest";

    private readonly ILogger<TinadecToolsProcessManager> _logger;
    private readonly string? _executablePath;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _defaultTimeout;
    private readonly string? _defaultWorkspaceRoot;

    private readonly object _stateLock = new();
    private readonly Dictionary<string, ManagedProcess> _processes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly Dictionary<string, SemaphoreSlim> _writeLocks = new(StringComparer.OrdinalIgnoreCase);
    private long _nextCallId;

    public TinadecToolsProcessManager(IConfiguration configuration, ILogger<TinadecToolsProcessManager> logger)
    {
        _logger = logger;
        _executablePath = configuration["TinadecTools:ExecutablePath"];
        if (string.IsNullOrWhiteSpace(_executablePath))
        {
            _executablePath = ProbeDefaultExecutable();
            if (_executablePath is not null)
                logger.LogInformation("TinadecTools executable was auto-detected at {Path}", _executablePath);
        }
        _startupTimeout = TimeSpan.FromSeconds(Double(configuration, "TinadecTools:StartupTimeoutSeconds", 30));
        _defaultTimeout = TimeSpan.FromSeconds(Double(configuration, "TinadecTools:DefaultTimeoutSeconds", 120));
        _defaultWorkspaceRoot = configuration["TinadecTools:DefaultWorkspaceRoot"];
    }

    /// <summary>
    /// Locates the TinadecTools apphost when no explicit path is configured: first
    /// next to the Core host (published or test layout), then inside a repository
    /// ancestor under TinadecTools/bin/{Debug|Release}/net10.0/.
    /// </summary>
    private static string? ProbeDefaultExecutable()
    {
        var executableName = OperatingSystem.IsWindows() ? "TinadecTools.exe" : "TinadecTools";
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, executableName) };

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var toolsDirectory = Path.Combine(directory.FullName, "TinadecTools");
            if (Directory.Exists(toolsDirectory))
            {
                foreach (var configuration in new[] { "Debug", "Release" })
                {
                    candidates.Add(Path.Combine(toolsDirectory, "bin", configuration, "net10.0", executableName));
                }
            }
            directory = directory.Parent;
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<ToolManifestDto> EnsureStartedAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        var root = ResolveRoot(workspaceRoot);
        var process = GetOrStart(root);
        return await process.ManifestTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ToolWireResponseDto> CallAsync(
        string workspaceRoot,
        ToolWireRequestDto request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var root = ResolveRoot(workspaceRoot);
        if (_executablePath is null)
        {
            return Error(-1, "process_exit", "TinadecTools executable path is not configured.");
        }

        var gate = GetWriteLock(root);
        ManagedProcess process;
        lock (_stateLock)
        {
            if (!_processes.TryGetValue(root, out process!) || process.Process.HasExited)
            {
                process = StartProcess(root);
            }
        }

        // Do not send regular work until the single shared handshake has completed.
        // This also surfaces a failed startup consistently to concurrent callers.
        await process.ManifestTask.WaitAsync(cancellationToken).ConfigureAwait(false);

        var callId = Interlocked.Increment(ref _nextCallId);
        var wire = new ToolWireRequestDto
        {
            ToolId = request.ToolId,
            SessionId = request.SessionId,
            ToolCallId = callId,
            Approved = request.Approved,
            Params = request.Params
        };

        var pending = process.RegisterPending(callId);
        var line = JsonSerializer.Serialize(wire);
        var effectiveTimeout = timeout ?? _defaultTimeout;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(effectiveTimeout);

        try
        {
            await gate.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            try
            {
                await process.StandardInput.WriteLineAsync(line.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }

            var completion = await pending.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            return completion;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.RemovePending(callId);
            return Error(callId, "timeout", $"Tool call timed out after {effectiveTimeout.TotalSeconds:0}s.");
        }
        catch (OperationCanceledException)
        {
            process.RemovePending(callId);
            throw;
        }
        catch (IOException ex)
        {
            process.RemovePending(callId);
            return Error(callId, "process_exit", "TinadecTools process pipe closed: " + ex.Message);
        }
    }

    public async Task<ToolManifestDto> GetManifestAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        var root = ResolveRoot(workspaceRoot);
        var process = GetOrStart(root);
        return await process.ManifestTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        List<ManagedProcess> stopped;
        lock (_stateLock)
        {
            stopped = _processes.Values.ToList();
            _processes.Clear();
        }
        foreach (var process in stopped)
        {
            process.FailPending("process_exit", "TinadecTools process was shut down by the host.");
            try
            {
                if (!process.Process.WaitForExit(3000)) process.Process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
            process.Dispose();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => ShutdownAsync(cancellationToken);

    public void Dispose() => ShutdownAsync().GetAwaiter().GetResult();

    private string ResolveRoot(string? workspaceRoot)
    {
        var root = string.IsNullOrWhiteSpace(workspaceRoot) ? _defaultWorkspaceRoot : workspaceRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("No workspace root was provided and TinadecTools:DefaultWorkspaceRoot is not configured.");
        }
        return Path.GetFullPath(root.Trim());
    }

    private ManagedProcess GetOrStart(string root)
    {
        lock (_stateLock)
        {
            if (_processes.TryGetValue(root, out var existing) && !existing.Process.HasExited) return existing;
            return StartProcess(root);
        }
    }

    private ManagedProcess StartProcess(string root)
    {
        if (_executablePath is null) throw new InvalidOperationException("TinadecTools executable path is not configured.");

        var startInfo = new ProcessStartInfo(_executablePath)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardInputEncoding = Utf8NoBom,
            CreateNoWindow = true
        };

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the TinadecTools process.");
        var managed = new ManagedProcess(process, root, _logger);

        // Publish the unique process before beginning its handshake. The handshake calls
        // this exact instance directly; it must never re-enter GetOrStart for the root.
        _processes[root] = managed;
        _ = Task.Run(() => ReadLoopAsync(managed));
        _ = Task.Run(() => DrainErrorAsync(managed));
        managed.ManifestTask = CallManifestAsync(managed);
        _logger.LogInformation("Started TinadecTools process {Pid} for workspace root {Root}", process.Id, root);
        return managed;
    }

    private async Task<ToolManifestDto> CallManifestAsync(ManagedProcess process)
    {
        try
        {
            var gate = GetWriteLock(process.Root);
            var callId = Interlocked.Increment(ref _nextCallId);
            var pending = process.RegisterPending(callId);
            var wire = new ToolWireRequestDto { ToolId = ManifestToolId, SessionId = "core", ToolCallId = callId, Approved = true };
            var line = JsonSerializer.Serialize(wire);
            using var cts = new CancellationTokenSource(_startupTimeout);
            await gate.WaitAsync(cts.Token).ConfigureAwait(false);
            try
            {
                await process.StandardInput.WriteLineAsync(line.AsMemory(), cts.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cts.Token).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
            var response = await pending.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            if (!response.IsSuccess || response.Result is null)
            {
                throw new InvalidDataException("TinadecTools manifest handshake failed.");
            }
            var manifest = JsonSerializer.Deserialize<ToolManifestDto>(response.Result.Value.GetRawText());
            if (manifest is null || manifest.ProtocolVersion is not (1 or 2))
            {
                throw new InvalidDataException("TinadecTools manifest protocol version is unsupported.");
            }
            return manifest;
        }
        catch (Exception ex)
        {
            RemoveFailedProcess(process, ex);
            throw;
        }
    }

    private void RemoveFailedProcess(ManagedProcess process, Exception exception)
    {
        _logger.LogWarning(exception, "TinadecTools manifest handshake failed for workspace root {Root}", process.Root);
        lock (_stateLock)
        {
            if (_processes.TryGetValue(process.Root, out var current) && ReferenceEquals(current, process))
            {
                _processes.Remove(process.Root);
            }
        }

        process.FailPending("process_exit", "TinadecTools manifest handshake failed.");
        try
        {
            if (!process.Process.HasExited)
            {
                process.Process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited while the handshake failure was being handled.
        }
        finally
        {
            process.Dispose();
        }
    }

    private async Task ReadLoopAsync(ManagedProcess managed)
    {
        try
        {
            while (!managed.Process.HasExited)
            {
                var line = await managed.Process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) continue;

                ToolWireResponseDto? response;
                try
                {
                    response = JsonSerializer.Deserialize<ToolWireResponseDto>(line);
                }
                catch (JsonException)
                {
                    _logger.LogDebug("Ignoring non-JSON line from TinadecTools process: {Line}", line);
                    continue;
                }
                if (response is null) continue;

                if (!managed.CompletePending(response.CallId, response))
                {
                    _logger.LogDebug("No pending call {CallId} for TinadecTools response", response.CallId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TinadecTools read loop ended for {Root}", managed.Root);
        }
        finally
        {
            managed.FailPending("process_exit", "The TinadecTools process exited before responding.");
        }
    }

    private async Task DrainErrorAsync(ManagedProcess managed)
    {
        try
        {
            while (!managed.Process.HasExited)
            {
                var line = await managed.Process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                if (line.Length != 0) _logger.LogDebug("TinadecTools stderr: {Line}", line);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TinadecTools stderr drain ended for {Root}", managed.Root);
        }
    }

    private SemaphoreSlim GetWriteLock(string root)
    {
        lock (_stateLock)
        {
            if (!_writeLocks.TryGetValue(root, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _writeLocks[root] = gate;
            }
            return gate;
        }
    }

    private static ToolWireResponseDto Error(long callId, string category, string message) => new()
    {
        CallId = callId,
        IsSuccess = false,
        Error = $"{category}: {message}"
    };

    private static double Double(IConfiguration configuration, string key, double fallback) =>
        double.TryParse(configuration[key], System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;

    private sealed class ManagedProcess : IDisposable
    {
        private readonly ILogger _logger;
        private readonly Dictionary<long, TaskCompletionSource<ToolWireResponseDto>> _pending = new();

        public ManagedProcess(Process process, string root, ILogger logger)
        {
            Process = process;
            Root = root;
            _logger = logger;
        }

        public Process Process { get; }
        public string Root { get; }
        public StreamWriter StandardInput => Process.StandardInput;
        public Task<ToolManifestDto> ManifestTask { get; set; } = null!;

        public TaskCompletionSource<ToolWireResponseDto> RegisterPending(long callId)
        {
            var tcs = new TaskCompletionSource<ToolWireResponseDto>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pending)
            {
                _pending[callId] = tcs;
            }
            return tcs;
        }

        public bool CompletePending(long callId, ToolWireResponseDto response)
        {
            TaskCompletionSource<ToolWireResponseDto>? tcs;
            lock (_pending)
            {
                if (!_pending.Remove(callId, out tcs)) return false;
            }
            return tcs!.TrySetResult(response);
        }

        public void RemovePending(long callId)
        {
            lock (_pending)
            {
                _pending.Remove(callId);
            }
        }

        public void FailPending(string category, string message)
        {
            List<TaskCompletionSource<ToolWireResponseDto>> waiting;
            lock (_pending)
            {
                waiting = _pending.Values.ToList();
                _pending.Clear();
            }
            foreach (var tcs in waiting)
            {
                tcs.TrySetResult(new ToolWireResponseDto { CallId = -1, IsSuccess = false, Error = $"{category}: {message}" });
            }
        }

        public void Dispose()
        {
            Process.Dispose();
        }
    }
}
