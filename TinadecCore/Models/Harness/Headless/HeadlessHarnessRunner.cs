using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Models.Harness.Acp;

namespace TinadecCore.Models.Harness.Headless;

/// <summary>
/// How to start one headless turn of a harness process. <c>StdinPayload</c> is written to the child's
/// standard input before it is closed; <c>null</c> leaves stdin unused.
/// </summary>
internal sealed record HarnessTurnRequest(
    string FileName,
    IReadOnlyList<string> ArgumentList,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?>? Environment,
    string? StdinPayload);

internal sealed record HarnessTurnOutcome(int? ExitCode, string Stderr);

/// <summary>
/// Seam that owns the process, so the envelope parsing and the fail-closed rules can be tested with
/// recorded vendor frames instead of a live harness.
/// </summary>
internal interface IHeadlessHarnessRunner
{
    Task<HarnessTurnOutcome> RunAsync(
        HarnessTurnRequest request,
        IHeadlessEnvelope envelope,
        Action<string>? textDelta,
        CancellationToken cancellationToken);
}

/// <summary>
/// Starts the harness as a child process and feeds its stdout to the envelope.
/// <para>
/// The launch shape is the one <see cref="ProcessAcpTransport"/> uses and for the same reasons:
/// arguments go through <see cref="ProcessStartInfo.ArgumentList"/> so a prompt never becomes a
/// command line, and stdout is read as bytes because the framing decides where a payload ends.
/// </para>
/// </summary>
internal sealed class ProcessHeadlessHarnessRunner : IHeadlessHarnessRunner
{
    /// <summary>
    /// stderr is kept only to explain a failed turn, so it is capped: a chatty vendor that dumps a
    /// stack trace per retry must not grow the response for a reason nobody reads.
    /// </summary>
    private const int StderrLimit = 4_000;

    public async Task<HarnessTurnOutcome> RunAsync(
        HarnessTurnRequest request,
        IHeadlessEnvelope envelope,
        Action<string>? textDelta,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            // Resolved below via HarnessProcessCommand.Apply.
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardInput = request.StdinPayload is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = NdjsonFraming.WireEncoding
        };
        HarnessProcessCommand.Apply(startInfo, request.FileName, request.ArgumentList);
        if (request.Environment is { } overrides)
        {
            foreach (var (key, value) in overrides)
            {
                if (value is null) startInfo.Environment.Remove(key);
                else startInfo.Environment[key] = value;
            }
        }

        startInfo.Environment.Remove("TINADEC_HOST_CONTROL_TOKEN");
        using var process = Start(startInfo, request.FileName);
        using var registration = cancellationToken.Register(
            static state => KillQuietly((Process)state!),
            process);

        var stderr = new StringBuilder();
        var stderrTask = DrainAsync(process.StandardError, stderr, cancellationToken);
        var stdoutTask = ReadStdoutAsync(process, envelope, textDelta, cancellationToken);

        // Only one of the two readers can be the exception the caller sees. Marking the other observed
        // keeps a cancelled turn from surfacing later as an unobserved-task crash that reads like a
        // harness fault rather than the user's own interrupt.
        ObserveQuietly(stderrTask);
        ObserveQuietly(stdoutTask);

        try
        {
            if (request.StdinPayload is { } payload)
            {
                await using (var stdin = process.StandardInput)
                {
                    await stdin.WriteAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await stdoutTask.ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return new HarnessTurnOutcome(process.HasExited ? process.ExitCode : null, Truncate(stderr.ToString(), StderrLimit));
        }
        finally
        {
            // Cancellation makes WaitForExitAsync(token) stop observing the process immediately. The
            // process tree and both pipe readers still own handles to the governed directory, though;
            // leaving them to ObserveQuietly is the leak that made the cancellation test fail during
            // DeleteTree. Terminate first, then await every reader and the parent before disposing the
            // Process wrapper.
            if (!process.HasExited) KillQuietly(process);
            await AwaitQuietlyAsync(stdoutTask).ConfigureAwait(false);
            await AwaitQuietlyAsync(stderrTask).ConfigureAwait(false);
            DisposeRedirectedStreams(process);
            await WaitForExitQuietlyAsync(process).ConfigureAwait(false);
        }
    }

    private static async Task ReadStdoutAsync(
        Process process,
        IHeadlessEnvelope envelope,
        Action<string>? textDelta,
        CancellationToken cancellationToken)
    {
        if (envelope.WholeDocument)
        {
            // ZCode's document spans ~26 lines and its first line is `{`; reading it as frames would
            // hand the parser nothing to parse and the run would fail as "the harness never answered".
            using var reader = new StreamReader(process.StandardOutput.BaseStream, NdjsonFraming.WireEncoding, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var document = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            envelope.Feed(document, textDelta);
            return;
        }

        await foreach (var frame in NdjsonFraming.ReadFramesAsync(process.StandardOutput.BaseStream, cancellationToken).ConfigureAwait(false))
        {
            envelope.Feed(frame, textDelta);
        }
    }

    private static async Task DrainAsync(StreamReader reader, StringBuilder sink, CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read <= 0) return;
            if (sink.Length < 4_000) sink.Append(buffer, 0, Math.Min(read, 4_000 - sink.Length));
        }
    }

    private static Process Start(ProcessStartInfo startInfo, string fileName)
    {
        try
        {
            return Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Starting '{fileName}' produced no process handle.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Core could not start the harness '{fileName}' for a headless turn: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Kills the whole tree: an npm-installed harness is a shim that spawns the real agent, and killing
    /// only the shim would leave the agent running against a stdout nobody reads.
    /// </summary>
    private static void KillQuietly(Process process)
    {
        try
        {
            if (process.HasExited) return;

            if (OperatingSystem.IsWindows())
            {
                // Process.Kill(entireProcessTree:true) returns after the root handle is signalled, but
                // Windows may still have a shim/grandchild holding the redirected pipe or cwd. taskkill
                // /T is the synchronous OS tree operation; waiting for its completion closes the race
                // between cancellation and the caller deleting the governed working directory.
                using var taskkill = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "taskkill.exe",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                taskkill.StartInfo.ArgumentList.Add("/PID");
                taskkill.StartInfo.ArgumentList.Add(process.Id.ToString());
                taskkill.StartInfo.ArgumentList.Add("/T");
                taskkill.StartInfo.ArgumentList.Add("/F");
                if (taskkill.Start()) taskkill.WaitForExit(10_000);
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                return;
            }

            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The caller is already cancelled; a child that refuses to die here is not a new error to report.
            _ = ex;
        }
    }

    private static void ObserveQuietly(Task task) => _ = task.ContinueWith(
        static faulted => _ = faulted.Exception,
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    private static async Task AwaitQuietlyAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    private static async Task WaitForExitQuietlyAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static void DisposeRedirectedStreams(Process process)
    {
        try { process.StandardInput.Dispose(); } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        try { process.StandardOutput.Dispose(); } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        try { process.StandardError.Dispose(); } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    private static string Truncate(string value, int limit) => value.Length <= limit ? value : value[..limit] + "…";
}
