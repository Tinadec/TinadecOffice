using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TinadecCore.Models.Harness.Acp;

/// <summary>
/// Spawns a harness as a stdio ACP server and hands its three streams to the protocol layer.
/// <para>
/// Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, never a joined command line:
/// a prompt is arbitrary user text, and quoting it into one string is how an argument becomes a
/// command. Standard output is read as raw bytes through <see cref="Stream.BaseStream"/> rather than
/// a <see cref="StreamReader"/>, because framing decides where a frame ends and a text reader would
/// decide it first.
/// </para>
/// <para>
/// Not handled here: the <c>cmd.exe /d /s /c</c> indirection that npm-installed harnesses want on
/// Windows. Measured on this host, spawning a <c>.cmd</c> directly through <see cref="Process"/>
/// does start, so this is not about reachability — it is about quoting. A batch shim re-parses its
/// argument line under cmd's rules, and a prompt containing <c>&amp;</c>, <c>|</c>, <c>%</c> or a
/// quote does not survive that unchanged. That belongs to <c>HarnessBinaryResolver</c>, which owns
/// the spawn shape, rather than being half-implemented here.
/// </para>
/// </summary>
internal sealed class ProcessAcpTransport : IAcpTransport
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    private readonly Process _process;
    private readonly Task _stderrDrain;
    private readonly ILogger? _logger;
    private int _killed;

    public string Description { get; }

    public Stream Stdin { get; }

    public Stream Stdout { get; }

    public Task Exited => _process.WaitForExitAsync();

    public ProcessAcpTransport(
        string fileName,
        IReadOnlyList<string> argumentList,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null,
        Action<string>? onStderr = null,
        ILogger? logger = null)
    {
        _logger = logger;
        Description = $"{fileName} {string.Join(' ', argumentList)}";

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Without these the child's output is decoded with the machine OEM codepage (GBK on
            // zh-CN Windows). Harmless here only because the protocol reads raw bytes; stderr is
            // read as text, and that is where it would matter.
            StandardErrorEncoding = NdjsonFraming.WireEncoding
        };
        HarnessProcessCommand.Apply(startInfo, fileName, argumentList);
        if (environment is { } overrides)
        {
            foreach (var (key, value) in overrides)
            {
                if (value is null) startInfo.Environment.Remove(key);
                else startInfo.Environment[key] = value;
            }
        }

        startInfo.Environment.Remove("TINADEC_HOST_CONTROL_TOKEN");
        try
        {
            _process = Process.Start(startInfo)
                ?? throw new AcpTransportException($"ACP harness '{fileName}' did not start (no process returned).");
        }
        catch (Win32Exception ex)
        {
            throw new AcpTransportException($"ACP harness '{fileName}' could not be started: {ex.Message}.", ex);
        }

        Stdin = _process.StandardInput.BaseStream;
        Stdout = _process.StandardOutput.BaseStream;

        // A stderr pipe nobody reads fills, and the child blocks on its next log line: for an ACP
        // agent that means a session that stops responding with no error anywhere. Drain it.
        _stderrDrain = Task.Run(() => DrainStderrAsync(_process, onStderr));
    }

    public void Kill()
    {
        if (Interlocked.Exchange(ref _killed, 1) != 0) return;
        try
        {
            // The harness may itself have spawned tool processes; a tree kill is what keeps an
            // aborted session from leaving grandchildren holding the workspace.
            _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            _logger?.LogDebug(ex, "ACP harness had already exited.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        try
        {
            _process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        await CliDrainShutdown.AwaitQuietlyAsync(_stderrDrain, DrainTimeout, _logger ?? NullLogger.Instance).ConfigureAwait(false);
        _process.Dispose();
    }

    private static async Task DrainStderrAsync(Process process, Action<string>? onStderr)
    {
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null) onStderr?.Invoke(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }
}

/// <summary>A harness process could not be started, or died while the session needed it.</summary>
internal sealed class AcpTransportException : InvalidOperationException
{
    public AcpTransportException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
