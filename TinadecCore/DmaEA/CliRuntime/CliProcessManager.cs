using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.DmaEA.CliRuntime;

/// <summary>Static description of a CLI runtime provider (persisted provider config projection).</summary>
public sealed record CliRuntimeConfig(
    string Driver,
    string BinaryPath,
    string? LaunchArgs = null,
    string? ServerUrl = null,
    string? HomePath = null);

/// <summary>A reachable CLI runtime endpoint the workbench can drive.</summary>
public sealed record CliRuntimeEndpoint(string Protocol, string ServerUrl, string? Token);

/// <summary>
/// Hosts one CLI server process per provider config. Reuses an already-running server on
/// <see cref="CliRuntimeConfig.ServerUrl"/> when reachable; otherwise spawns the binary with a
/// free port, parses the port/token the agent prints on stdout, and polls HTTP readiness.
/// Spawned processes are killed on host disposal so no orphaned agent servers survive shutdown.
/// </summary>
public interface ICliProcessManager
{
    Task<CliRuntimeEndpoint> EnsureRunningAsync(CliRuntimeConfig config, CancellationToken cancellationToken = default);
}

internal sealed class CliProcessManager : ICliProcessManager, IAsyncDisposable
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, HostedCli> _processes = new(StringComparer.Ordinal);
    private readonly HttpClient _http = new() { Timeout = ProbeTimeout };
    private readonly string _logDirectory;
    private readonly ILogger<CliProcessManager> _logger;

    public CliProcessManager(ILogger<CliProcessManager> logger)
    {
        _logDirectory = Path.Combine(Path.GetTempPath(), "tinadec-cli-runtime");
        Directory.CreateDirectory(_logDirectory);
        _logger = logger;
    }

    public async Task<CliRuntimeEndpoint> EnsureRunningAsync(CliRuntimeConfig config, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(config.ServerUrl) && await IsReachableAsync(config.ServerUrl, cancellationToken).ConfigureAwait(false))
        {
            return new CliRuntimeEndpoint(ProtocolFor(config), config.ServerUrl!, null);
        }

        if (string.IsNullOrWhiteSpace(config.BinaryPath)) throw new InvalidOperationException($"CLI runtime '{config.Driver}' has no binary_path and its server_url is not reachable.");

        var key = $"{config.Driver}|{config.BinaryPath}|{config.LaunchArgs}";
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
                    _logger.LogDebug(ex, "CLI process already exited.");
                }
                hosted.Drainer.Dispose();
            }
            _processes.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HostedCli> SpawnAsync(CliRuntimeConfig config, CancellationToken cancellationToken)
    {
        var port = ExtractPort(config.LaunchArgs) ?? FreePort();
        var serverUrl = $"http://127.0.0.1:{port}";
        var launchArgs = config.LaunchArgs ?? "";
        if (launchArgs.Length == 0 || !Regex.IsMatch(launchArgs, @"--(?:acp-)?port\s+\d+", RegexOptions.IgnoreCase))
        {
            launchArgs = $"{launchArgs} --acp-port {port}".TrimStart();
        }

        var psi = new ProcessStartInfo(config.BinaryPath, launchArgs)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!string.IsNullOrWhiteSpace(config.HomePath)) psi.Environment["HOME"] = config.HomePath;

        var logPath = Path.Combine(_logDirectory, $"{config.Driver}-{Environment.ProcessId}-{port}.log");
        var log = File.CreateText(logPath);
        var ports = new ConcurrentQueue<string>();
        var tokens = new ConcurrentQueue<string>();
        var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"CLI runtime '{config.Driver}' binary_path '{config.BinaryPath}' could not be started: {ex.Message}");
        }

        var drainer = Task.Run(() => DrainAsync(process, log, ports, tokens, logPath), cancellationToken);
        var observedPort = await FirstValueAsync(ports, process, StartTimeout, cancellationToken).ConfigureAwait(false);
        var token = await FirstValueAsync(tokens, process, StartTimeout, cancellationToken).ConfigureAwait(false);
        if (observedPort is not null && int.TryParse(observedPort, out var parsed) && parsed != port)
        {
            serverUrl = $"http://127.0.0.1:{parsed}";
        }

        var deadline = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited) throw new InvalidOperationException($"CLI runtime '{config.Driver}' exited during startup (code {process.ExitCode}). See {logPath}");
            if (await IsReachableAsync(serverUrl, cancellationToken).ConfigureAwait(false)) break;
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
        if (!await IsReachableAsync(serverUrl, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException($"CLI runtime '{config.Driver}' did not become reachable at {serverUrl} within {StartTimeout.TotalSeconds}s. See {logPath}");

        _logger.LogInformation("CLI runtime '{Driver}' running at {ServerUrl} (log {LogPath}).", config.Driver, serverUrl, logPath);
        return new HostedCli(process, drainer, new CliRuntimeEndpoint(ProtocolFor(config), serverUrl, token));
    }

    private static string ProtocolFor(CliRuntimeConfig config) => config.Driver switch
    {
        "opencode" => ChatProtocols.OpencodeServe,
        _ => ChatProtocols.Acp
    };

    private async Task DrainAsync(Process process, StreamWriter log, ConcurrentQueue<string> ports, ConcurrentQueue<string> tokens, string logPath)
    {
        await Task.WhenAll(DrainStreamAsync(process.StandardOutput, true), DrainStreamAsync(process.StandardError, false)).ConfigureAwait(false);
        log.Dispose();

        async Task DrainStreamAsync(StreamReader stream, bool capture)
        {
            try
            {
                string? line;
                while ((line = await stream.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    await log.WriteLineAsync(line).ConfigureAwait(false);
                    await log.FlushAsync().ConfigureAwait(false);
                    if (!capture) continue;
                    var portMatch = Regex.Match(line, @"(?:port\s*[:=]\s*(\d+)|http://127\.0\.0\.1:(\d+))", RegexOptions.IgnoreCase);
                    if (portMatch.Success) ports.Enqueue(portMatch.Groups[1].Success ? portMatch.Groups[1].Value : portMatch.Groups[2].Value);
                    var tokenMatch = Regex.Match(line, @"(?<q>"")?token(?<colon>\s*[:=]\s*|\s+)(?(q)""([^""]+)""|([A-Za-z0-9._~+/=-]{16,}))", RegexOptions.IgnoreCase);
                    if (tokenMatch.Success) tokens.Enqueue(tokenMatch.Groups[2].Success ? tokenMatch.Groups[2].Value : tokenMatch.Groups[3].Value);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _logger.LogDebug(ex, "CLI stdout drain ended for {LogPath}.", logPath);
            }
        }
    }

    private static async Task<string?> FirstValueAsync(ConcurrentQueue<string> values, Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (values.TryDequeue(out var value)) return value;
            if (process.HasExited) return null;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private async Task<bool> IsReachableAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }

    private static int? ExtractPort(string? launchArgs)
    {
        if (string.IsNullOrWhiteSpace(launchArgs)) return null;
        var match = Regex.Match(launchArgs, @"--(?:acp-)?port\s+(\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var port) ? port : null;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed record HostedCli(Process Process, Task Drainer, CliRuntimeEndpoint Endpoint)
    {
        public bool IsAlive => !Process.HasExited;
    }
}
