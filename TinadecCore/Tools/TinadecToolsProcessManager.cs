using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Persistence;

namespace TinadecCore.Tools;

/// <summary>
/// Manages one TinadecTools child process per workspace root, speaking the
/// line-delimited JSON protocol over redirected stdio. Calls are correlated by
/// an incrementing call id; a single write lock serializes request lines; a
/// per-process read loop dispatches responses. Crashed processes fail their
/// in-flight calls with a structured process_exit result and are restarted on
/// the next call.
/// </summary>
/// <summary>
/// Default local-process implementation of <see cref="IToolProvider"/>.
/// The process-specific compatibility port is implemented as well so existing
/// embedders can migrate without changing their registrations in one release.
/// </summary>
public sealed class TinadecToolsProcessManager : IToolProcessManager, IToolExecutionContextLifecycle, IHostedService, IDisposable
{
    private const string ManifestToolId = "#manifest";

    private readonly ILogger<TinadecToolsProcessManager> _logger;
    private readonly string? _executablePath;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _defaultTimeout;
    private readonly string? _defaultWorkspaceRoot;
    private readonly string? _additionalReadRoots;
    private readonly string _userStorageRoot;

    private readonly object _stateLock = new();
    private static readonly StringComparer ProcessPathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly Dictionary<string, ManagedProcess> _processes = new(ProcessPathComparer);
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly Dictionary<string, SemaphoreSlim> _writeLocks = new(ProcessPathComparer);
    private long _nextCallId;
    private readonly Dictionary<string, HashSet<string>> _contextRoots = new(StringComparer.Ordinal);

    public async Task ReleaseAsync(string runId, CancellationToken cancellationToken = default)
    {
        string[] roots;
        lock (_stateLock)
        {
            if (!_contextRoots.Remove(runId, out var owned)) return;
            roots = owned.Where(root => _processes.TryGetValue(root, out var process) && !process.Process.HasExited).ToArray();
        }
        foreach (var root in roots)
            try
            {
                ManagedProcess? owned;
                lock (_stateLock) _processes.TryGetValue(root, out owned);
                if (owned is null || owned.Process.HasExited) continue;
                var response = await CallAsync(owned.Root, new ToolWireRequestDto { ToolId = "#release_execution_context", SessionId = "context-release", Approved = false, Params = JsonSerializer.SerializeToElement(new { run_id = runId }) }, TimeSpan.FromSeconds(10), null, cancellationToken, root).ConfigureAwait(false);
                if (!response.IsSuccess) throw new InvalidOperationException("The provider rejected execution-context release.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Terminal reconciliation must still find every unreleased root
                // after host shutdown interrupts a best-effort cleanup pass.
                lock (_stateLock)
                {
                    if (!_contextRoots.TryGetValue(runId, out var retry)) _contextRoots[runId] = retry = new(ProcessPathComparer);
                    foreach (var ownedRoot in roots) retry.Add(ownedRoot);
                }
                throw;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                lock (_stateLock) { if (!_contextRoots.TryGetValue(runId, out var retry)) _contextRoots[runId] = retry = new(ProcessPathComparer); retry.Add(root); }
                _logger.LogDebug(ex, "Could not release tool context {RunId}.", runId);
            }
    }

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
        _additionalReadRoots = configuration[ToolHostEnvironment.AdditionalReadRootsConfigurationKey];
        _userStorageRoot = StorageScopePaths.User(configuration["TinadecStorage:UserRoot"]).Root;
    }

    /// <summary>
    /// Locates the TinadecTools apphost when no explicit path is configured: first
    /// next to the Core host (published or test layout), then inside a repository
    /// ancestor under TinadecTools/bin/{Debug|Release}/{net11.0|net10.0}/.
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
                foreach (var tfm in new[] { "net11.0", "net10.0" })
                {
                    foreach (var configuration in new[] { "Debug", "Release" })
                    {
                        candidates.Add(Path.Combine(toolsDirectory, "bin", configuration, tfm, executableName));
                    }
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

    public Task<ToolWireResponseDto> CallAsync(
        string workspaceRoot,
        ToolWireRequestDto request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        CallAsync(workspaceRoot, request, timeout, observer: null, cancellationToken);

    public Task<ToolWireResponseDto> CallForRunAsync(
        string runId,
        string workspaceRoot,
        ToolWireRequestDto request,
        CancellationToken cancellationToken = default) =>
        CallAsync(workspaceRoot, request, timeout: null, observer: null, cancellationToken, ResolveOwnedProcessKey(runId));

    /// <summary>
    /// The live process this run already owns, or null when the run has none (a host restart
    /// clears the map, and the authoritative terminal sessions live in the Tools child, so they
    /// are gone with it). Null keeps the ordinary context-hash resolution.
    /// </summary>
    internal string? ResolveOwnedProcessKey(string runId)
    {
        lock (_stateLock)
            return _contextRoots.TryGetValue(runId, out var owned)
                ? owned.FirstOrDefault(key => _processes.TryGetValue(key, out var process) && !process.Process.HasExited)
                : null;
    }

    /// <summary>
    /// The identity of the child a context selects. Only the fields that change the
    /// child's grant boundary take part; a control call that re-derives a context can
    /// drift them, which is why terminal control reuses the run's owned key instead.
    /// </summary>
    internal static string ComputeProcessKey(string root, ToolExecutionContextDto? context) =>
        context is null ? root : root + "|" + ToolSettingsSchema.Hash(JsonSerializer.SerializeToElement(new { context.StorageId, context.WorkspaceRoots, context.WorkspaceFiles, context.PrimaryRootId, context.ProjectStorageWrite }));

    /// <summary>Every key the run owns. Terminal control must not add a second one.</summary>
    internal string[] OwnedProcessKeysForTests(string runId)
    {
        lock (_stateLock)
            return _contextRoots.TryGetValue(runId, out var owned) ? owned.ToArray() : [];
    }

    public Task<ToolWireResponseDto> CallStreamingAsync(
        string workspaceRoot,
        ToolWireRequestDto request,
        TimeSpan? timeout,
        Action<ToolWireEventDto>? onEvent,
        CancellationToken cancellationToken = default) =>
        CallAsync(workspaceRoot, request, timeout, onEvent, cancellationToken);

    /// <summary>
    /// Raised for wire events that carry no in-flight call id (broadcast), e.g. the
    /// exit of a long-lived terminal session. Subscribers must be exception-safe.
    /// </summary>
    public event Action<ToolWireEventDto>? WireEventBroadcast;

    private async Task<ToolWireResponseDto> CallAsync(
        string workspaceRoot,
        ToolWireRequestDto request,
        TimeSpan? timeout,
        Action<ToolWireEventDto>? observer,
        CancellationToken cancellationToken, string? ownedProcessKey = null)
    {
        var root = ResolveRoot(workspaceRoot);
        var processKey = ownedProcessKey ?? ComputeProcessKey(root, request.ExecutionContext);
        if (!string.IsNullOrWhiteSpace(request.ExecutionContext?.RunId))
            lock (_stateLock)
            {
                if (!_contextRoots.TryGetValue(request.ExecutionContext.RunId, out var owned)) _contextRoots[request.ExecutionContext.RunId] = owned = new(ProcessPathComparer);
                owned.Add(processKey);
            }
        if (_executablePath is null)
        {
            return Error(-1, "process_exit", "TinadecTools executable path is not configured.");
        }

        var gate = GetWriteLock(processKey);
        ManagedProcess process;
        lock (_stateLock)
        {
            if (!_processes.TryGetValue(processKey, out process!) || process.Process.HasExited)
            {
                process = StartProcess(root, processKey);
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
            Params = request.Params,
            ExecutionContext = request.ExecutionContext
        };

        var pending = process.RegisterPending(callId, observer);
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
            _writeLocks.Clear();
        }
        if (stopped.Count == 0) return;

        // Fail pending RPC calls and close every child's stdin simultaneously first so
        // TinadecTools receives EOF immediately and runs McpRuntime.DisposeAsync() in parallel.
        foreach (var process in stopped)
        {
            process.FailPending("process_exit", "TinadecTools process was shut down by the host.");
            try
            {
                process.StandardInput.Close();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        await Task.WhenAll(stopped.Select(StopProcessAsync)).ConfigureAwait(false);
    }

    private static async Task StopProcessAsync(ManagedProcess process)
    {
        try
        {
            if (!process.Process.HasExited)
            {
                using var graceCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
                try
                {
                    await process.Process.WaitForExitAsync(graceCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (!process.Process.HasExited)
                    {
                        process.Process.Kill(entireProcessTree: true);
                    }
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Process exited concurrently with termination.
        }
        finally
        {
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

    private ManagedProcess StartProcess(string root, string? processKey = null)
    {
        processKey ??= root;
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
        startInfo.Environment.Remove("TINADEC_HOST_CONTROL_TOKEN");
        startInfo.Environment["TINADEC_HOME"] = _userStorageRoot;

        // The frozen call envelope supplies all authorized source roots. Host-owned
        // credential paths are fixed at startup and cannot be supplied by tool params.
        if (!string.IsNullOrWhiteSpace(_additionalReadRoots))
        {
            startInfo.Environment[ToolHostEnvironment.AdditionalReadRootsVariable] = _additionalReadRoots;
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the TinadecTools process.");
        var managed = new ManagedProcess(process, root, _logger, RaiseBroadcast);
        managed.Key = processKey;

        // Publish the unique process before beginning its handshake. The handshake calls
        // this exact instance directly; it must never re-enter GetOrStart for the root.
        _processes[processKey] = managed;
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
            var gate = GetWriteLock(process.Key);
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
        _logger.TryLogWarning(exception, "TinadecTools manifest handshake failed for workspace root {Root}", process.Root);
        lock (_stateLock)
        {
            if (_processes.TryGetValue(process.Key, out var current) && ReferenceEquals(current, process))
            {
                _processes.Remove(process.Key);
                _writeLocks.Remove(process.Key);
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

                // Unsolicited notifications are distinguished from responses by
                // their "kind" marker; they must never complete a pending call.
                if (TryParseWireEvent(line, out var wireEvent))
                {
                    managed.RaiseEvent(wireEvent!);
                    continue;
                }

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

    /// <summary>
    /// Detects a protocol v2 notification line. Responses carry no "kind" property,
    /// so the marker is unambiguous and cannot be spoofed by tool results.
    /// </summary>
    private static bool TryParseWireEvent(string line, out ToolWireEventDto? wireEvent)
    {
        wireEvent = null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("kind", out var kind)
                || kind.ValueKind != JsonValueKind.String
                || !string.Equals(kind.GetString(), "event", StringComparison.Ordinal))
            {
                return false;
            }

            long callId = 0;
            if (root.TryGetProperty("call_id", out var callIdElement)
                && callIdElement.ValueKind == JsonValueKind.Number)
            {
                callId = callIdElement.TryGetInt64(out var parsed) ? parsed : 0;
            }

            wireEvent = new ToolWireEventDto
            {
                CallId = callId,
                Event = root.TryGetProperty("event", out var name) && name.ValueKind == JsonValueKind.String
                    ? name.GetString() ?? string.Empty
                    : string.Empty,
                Payload = root.TryGetProperty("payload", out var payload) ? payload.Clone() : null
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void RaiseBroadcast(ToolWireEventDto wireEvent)
    {
        var handler = WireEventBroadcast;
        if (handler is null) return;
        foreach (var subscriber in handler.GetInvocationList().OfType<Action<ToolWireEventDto>>())
        {
            try
            {
                subscriber(wireEvent);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Tool wire event broadcast subscriber failed for {Event}", wireEvent.Event);
            }
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
        private readonly Action<ToolWireEventDto> _broadcast;
        private readonly Dictionary<long, TaskCompletionSource<ToolWireResponseDto>> _pending = new();
        private readonly Dictionary<long, Action<ToolWireEventDto>> _observers = new();

        public ManagedProcess(Process process, string root, ILogger logger, Action<ToolWireEventDto> broadcast)
        {
            Process = process;
            Root = root;
            _logger = logger;
            _broadcast = broadcast;
        }

        public Process Process { get; }
        public string Root { get; }
        public string Key { get; set; } = string.Empty;
        public StreamWriter StandardInput => Process.StandardInput;
        public Task<ToolManifestDto> ManifestTask { get; set; } = null!;

        public TaskCompletionSource<ToolWireResponseDto> RegisterPending(
            long callId,
            Action<ToolWireEventDto>? observer = null)
        {
            var tcs = new TaskCompletionSource<ToolWireResponseDto>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pending)
            {
                _pending[callId] = tcs;
                if (observer is not null) _observers[callId] = observer;
            }
            return tcs;
        }

        /// <summary>
        /// Routes an unsolicited wire event. Events tied to a live call go to that
        /// call's observer; the rest are broadcast (long-lived session lifecycle).
        /// </summary>
        public void RaiseEvent(ToolWireEventDto wireEvent)
        {
            Action<ToolWireEventDto>? observer = null;
            lock (_pending)
            {
                if (wireEvent.CallId > 0 && _observers.TryGetValue(wireEvent.CallId, out var found))
                {
                    observer = found;
                }
            }

            if (observer is not null)
            {
                try
                {
                    observer(wireEvent);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Tool wire event observer failed for {Event}", wireEvent.Event);
                }
                return;
            }

            try
            {
                _broadcast(wireEvent);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Tool wire event broadcast failed for {Event}", wireEvent.Event);
            }
        }

        public bool CompletePending(long callId, ToolWireResponseDto response)
        {
            TaskCompletionSource<ToolWireResponseDto>? tcs;
            lock (_pending)
            {
                if (!_pending.Remove(callId, out tcs)) return false;
                _observers.Remove(callId);
            }
            return tcs!.TrySetResult(response);
        }

        public void RemovePending(long callId)
        {
            lock (_pending)
            {
                _pending.Remove(callId);
                _observers.Remove(callId);
            }
        }

        public void FailPending(string category, string message)
        {
            List<TaskCompletionSource<ToolWireResponseDto>> waiting;
            lock (_pending)
            {
                waiting = _pending.Values.ToList();
                _pending.Clear();
                _observers.Clear();
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
