using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace TinadecCore.Models.Harness;

/// <summary>
/// What the caller knows about an <c>opencode serve</c> runtime: the binary, the argv that starts the
/// server, and the URL to try reusing before starting anything. There is no driver field because this
/// host serves exactly one harness shape; every other local agent goes through the ACP session host.
/// </summary>
public sealed record OpencodeServeConfig(
    string BinaryPath,
    string? LaunchArgs = null,
    string? ServerUrl = null,
    string? HomePath = null);

/// <summary>A reachable opencode serve endpoint. Carries no token: opencode serve issues none, and
/// scraping stdout for a secret is how a credential ends up in a log file.</summary>
public sealed record OpencodeServeEndpoint(string ServerUrl);

/// <summary>
/// Hosts one <c>opencode serve</c> process per (binary, argv) pair. Reuses an already-running server on
/// <see cref="OpencodeServeConfig.ServerUrl"/> when it answers; otherwise spawns the binary on a free
/// port and polls HTTP readiness. Spawned processes are killed on host disposal so no orphaned agent
/// server survives shutdown.
/// </summary>
public interface IOpencodeServeProcessManager
{
    Task<OpencodeServeEndpoint> EnsureRunningAsync(OpencodeServeConfig config, CancellationToken cancellationToken = default);
}

internal sealed class OpencodeServeProcessManager : IOpencodeServeProcessManager, IAsyncDisposable
{
    private const string PortPlaceholder = "{port}";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly Regex ExplicitPort = new(@"--port\s+(?<port>\d+)", RegexOptions.IgnoreCase);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, HostedServe> _processes = new(StringComparer.Ordinal);
    private readonly TimeSpan _probeTimeout;
    private readonly HttpClient _http;
    private readonly string _logDirectory;
    private readonly ILogger<OpencodeServeProcessManager> _logger;

    public OpencodeServeProcessManager(ILogger<OpencodeServeProcessManager> logger)
        : this(logger, ProbeTimeout)
    {
    }

    internal OpencodeServeProcessManager(ILogger<OpencodeServeProcessManager> logger, TimeSpan probeTimeout)
    {
        _probeTimeout = probeTimeout > TimeSpan.Zero ? probeTimeout : ProbeTimeout;
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _logDirectory = Path.Combine(Path.GetTempPath(), "tinadec-opencode-serve");
        Directory.CreateDirectory(_logDirectory);
        _logger = logger;
    }

    public async Task<OpencodeServeEndpoint> EnsureRunningAsync(OpencodeServeConfig config, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(config.ServerUrl))
        {
            var probe = await ProbeUrlAsync(config.ServerUrl, cancellationToken).ConfigureAwait(false);
            if (probe == ProbeOutcome.Reachable)
            {
                return new OpencodeServeEndpoint(config.ServerUrl!);
            }
            if (probe == ProbeOutcome.TimedOut)
            {
                throw new InvalidOperationException(
                    $"opencode serve probe for '{config.ServerUrl}' timed out after {_probeTimeout.TotalSeconds:0.##}s without an HTTP response.");
            }
        }

        if (string.IsNullOrWhiteSpace(config.BinaryPath))
            throw new InvalidOperationException(
                $"opencode serve has no binary_path and its server_url {(string.IsNullOrWhiteSpace(config.ServerUrl) ? "is not set" : $"'{config.ServerUrl}' is not reachable")}.");

        var key = $"{config.BinaryPath}|{config.LaunchArgs}";
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_processes.TryGetValue(key, out var hosted) && hosted.IsAlive) return hosted.Endpoint;
            hosted = await SpawnAsync(config, cancellationToken).ConfigureAwait(false);
            _processes[key] = hosted;
            return hosted.Endpoint;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var hosted in _processes.Values)
            {
                try
                {
                    hosted.Process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
                {
                    _logger.LogDebug(ex, "opencode serve process already exited.");
                }

                await CliDrainShutdown.AwaitQuietlyAsync(hosted.Drainer, DrainTimeout, _logger).ConfigureAwait(false);
            }

            _processes.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HostedServe> SpawnAsync(OpencodeServeConfig config, CancellationToken cancellationToken)
    {
        var (serverUrl, launchArgs) = Plan(config.LaunchArgs);
        var port = new Uri(serverUrl).Port;

        var psi = new ProcessStartInfo(config.BinaryPath, launchArgs)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!string.IsNullOrWhiteSpace(config.HomePath)) psi.Environment["HOME"] = config.HomePath;
        psi.Environment.Remove("TINADEC_HOST_CONTROL_TOKEN");

        var logPath = Path.Combine(_logDirectory, $"opencode-serve-{Environment.ProcessId}-{port}.log");
        var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"opencode serve binary '{config.BinaryPath}' could not be started: {ex.Message}");
        }

        // The log is the only diagnosis when a server never becomes reachable, so the streams are
        // drained to a file rather than left to fill the pipe and block the child. The drainer owns
        // closing it: disposing here would truncate the file while a healthy server is still writing.
        var log = File.CreateText(logPath);
        var drainer = Task.Run(() => DrainAsync(process, log, logPath), cancellationToken);

        var deadline = DateTime.UtcNow + StartTimeout;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                    throw new InvalidOperationException($"opencode serve exited during startup (code {process.ExitCode}). See {logPath}");

                if (await IsReachableAsync(serverUrl, cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogInformation("opencode serve running at {ServerUrl} (log {LogPath}).", serverUrl, logPath);
                    return new HostedServe(process, drainer, new OpencodeServeEndpoint(serverUrl));
                }

                if (DateTime.UtcNow >= deadline)
                    throw new InvalidOperationException($"opencode serve did not become reachable at {serverUrl} within {StartTimeout.TotalSeconds}s. See {logPath}");

                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            await AbandonAsync(process, drainer).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// A child left running because its server never answered is worse than a slow failure. The host's
    /// registry only ever records successes, so an unregistered process holding a loopback port, its
    /// own credentials, and two redirected pipes is invisible to every later shutdown path — including
    /// disposal, which is how a test run ended up with an orphan outliving its host.
    /// </summary>
    private async Task AbandonAsync(Process process, Task drainer)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            _logger.LogDebug(ex, "opencode serve child had already exited.");
        }

        await CliDrainShutdown.AwaitQuietlyAsync(drainer, DrainTimeout, _logger).ConfigureAwait(false);
    }

    /// <summary>
    /// Decides the port before spawning and never revises it from the child's stdout. The old host
    /// started with <c>--port 0</c> and scraped the printed port back out of the log, which meant a
    /// harness that changed its banner format stopped being reachable — and the same regexes were
    /// harvesting a bearer token out of the output.
    /// </summary>
    private static (string ServerUrl, string LaunchArgs) Plan(string? launchArgs)
    {
        var args = string.IsNullOrWhiteSpace(launchArgs) ? "serve" : launchArgs!;
        var explicitPort = ExplicitPort.Match(args);
        var port = explicitPort.Success ? int.Parse(explicitPort.Groups["port"].Value, System.Globalization.CultureInfo.InvariantCulture) : FreePort();

        if (args.Contains(PortPlaceholder, StringComparison.Ordinal))
            args = args.Replace(PortPlaceholder, port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        else if (!explicitPort.Success)
            args = $"{args} --port {port}";

        return ($"http://127.0.0.1:{port}", args.Trim());
    }

    private async Task DrainAsync(Process process, StreamWriter log, string logPath)
    {
        await Task.WhenAll(DrainStreamAsync(process.StandardOutput), DrainStreamAsync(process.StandardError)).ConfigureAwait(false);
        log.Dispose();

        async Task DrainStreamAsync(StreamReader stream)
        {
            try
            {
                string? line;
                while ((line = await stream.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    await log.WriteLineAsync(line).ConfigureAwait(false);
                    await log.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _logger.LogDebug(ex, "opencode serve output drain ended for {LogPath}.", logPath);
            }
        }
    }

    private enum ProbeOutcome
    {
        Reachable,
        NotListening,
        TimedOut
    }

    private async Task<ProbeOutcome> ProbeUrlAsync(string url, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_probeTimeout);
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
            // Any HTTP answer means a server is listening; the status is the harness's business, and a
            // 404 from the root path is still a running server.
            return ProbeOutcome.Reachable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException or TimeoutException)
        {
            return ProbeOutcome.TimedOut;
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException)
        {
            return ProbeOutcome.NotListening;
        }
    }

    private async Task<bool> IsReachableAsync(string url, CancellationToken cancellationToken) =>
        await ProbeUrlAsync(url, cancellationToken).ConfigureAwait(false) == ProbeOutcome.Reachable;

    private static int FreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed record HostedServe(Process Process, Task Drainer, OpencodeServeEndpoint Endpoint)
    {
        public bool IsAlive => !Process.HasExited;
    }
}
