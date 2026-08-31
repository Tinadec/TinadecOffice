using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinadecTools.Runtime;

// ── 会话模型 ──────────────────────────────────────────────────────────────────

/// <summary>A long-lived terminal session hosted inside the TinadecTools process.</summary>
public sealed class TerminalSession
{
    public required string TerminalSessionId { get; init; }
    public required string Command { get; init; }
    public required string WorkingDirectory { get; init; }
    public required Process Process { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    /// <summary>False while the session is still tracked as a live call attachment.</summary>
    public volatile bool Exited;
    public int ExitCode = -1;
    public bool TimedOut;
    /// <summary>The in-flight tool call id currently attached to this session (-1 = detached/broadcast).</summary>
    public long AttachedCallId = -1;
    /// <summary>Completes when both output pumps have flushed their last event.</summary>
    public Task OutputDrained { get; set; } = Task.CompletedTask;
    /// <summary>Bounded replay buffer of recent output for late subscribers.</summary>
    public readonly ConcurrentQueue<string> Replay = new();
    public long ReplayBytes;
}

public sealed record TerminalSessionSnapshot(
    string TerminalSessionId,
    string Command,
    string WorkingDirectory,
    DateTimeOffset StartedAt,
    bool Exited,
    int ExitCode,
    bool TimedOut);

/// <summary>
/// Hosts long-lived terminal sessions ("dev server" style commands) inside the
/// TinadecTools child process. Output is streamed to Core as line-delimited
/// <c>terminal.stdout</c> wire events; sessions survive after the originating
/// tool call returns and are addressable via the reserved <c>#terminal</c> tool.
/// </summary>
public static class TerminalSessionHost
{
    private const int MaxSessions = 16;
    private const int MaxReplayBytes = 256 * 1024;
    private const int MaxEventChunkChars = 8 * 1024;

    private static readonly ConcurrentDictionary<string, TerminalSession> Sessions = new(StringComparer.Ordinal);

    private static readonly object ConsoleLock = new();

    /// <summary>
    /// Emits one wire event line on stdout. call_id &lt;= 0 means broadcast.
    /// The line is written with <see cref="Utf8JsonWriter"/> because this host
    /// disables reflection-based serialization, and because the payload is
    /// small and fixed-shaped.
    /// </summary>
    internal static void EmitEvent(long callId, string eventName, Action<Utf8JsonWriter> writePayload)
    {
        using var stream = new MemoryStream(512);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "event");
            writer.WriteNumber("call_id", callId);
            writer.WriteString("event", eventName);
            writer.WritePropertyName("payload");
            writer.WriteStartObject();
            writePayload(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        var line = Encoding.UTF8.GetString(stream.ToArray());
        lock (ConsoleLock)
        {
            Console.WriteLine(line);
            Console.Out.Flush();
        }
    }

    public static IReadOnlyList<TerminalSessionSnapshot> ListSnapshots() =>
        Sessions.Values.OrderBy(s => s.StartedAt).Select(ToSnapshot).ToList();

    public static TerminalSessionSnapshot? Find(string terminalSessionId) =>
        Sessions.TryGetValue(terminalSessionId, out var session) ? ToSnapshot(session) : null;

    private static TerminalSessionSnapshot ToSnapshot(TerminalSession session) => new(
        session.TerminalSessionId,
        session.Command,
        session.WorkingDirectory,
        session.StartedAt,
        session.Exited,
        session.ExitCode,
        session.TimedOut);

    public static bool TryWriteStdin(string terminalSessionId, string data)
    {
        if (!Sessions.TryGetValue(terminalSessionId, out var session) || session.Exited)
            return false;
        try
        {
            session.Process.StandardInput.Write(data);
            session.Process.StandardInput.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool TryKill(string terminalSessionId)
    {
        if (!Sessions.TryGetValue(terminalSessionId, out var session))
            return false;
        KillSafe(session.Process);
        return true;
    }

    /// <summary>Removes exited sessions from the registry (housekeeping).</summary>
    public static void PruneExited()
    {
        foreach (var (id, session) in Sessions)
        {
            if (session.Exited && session.StartedAt < DateTimeOffset.UtcNow.AddMinutes(-5))
            {
                Sessions.TryRemove(id, out _);
            }
        }
    }

    internal static bool CanAdmit => Sessions.Count(s => !s.Value.Exited) < MaxSessions;

    internal static void Register(TerminalSession session) => Sessions[session.TerminalSessionId] = session;

    internal static void AppendReplay(TerminalSession session, string chunk)
    {
        lock (session.Replay)
        {
            session.Replay.Enqueue(chunk);
            session.ReplayBytes += chunk.Length;
            while (session.ReplayBytes > MaxReplayBytes && session.Replay.TryDequeue(out var dropped))
            {
                session.ReplayBytes -= dropped.Length;
            }
        }
    }

    internal static string ReadReplay(TerminalSession session)
    {
        var builder = new StringBuilder();
        lock (session.Replay)
        {
            foreach (var chunk in session.Replay) builder.Append(chunk);
        }
        return builder.ToString();
    }

    internal static void KillSafe(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* best-effort */ }
    }

    /// <summary>Pumps stdout/stderr of a process as terminal.stdout wire events.</summary>
    internal static void StartOutputPumps(
        TerminalSession session,
        Func<long> attachedCallId,
        CancellationToken cancellationToken)
    {
        session.OutputDrained = Task.WhenAll(
            PumpStreamAsync(session, session.Process.StandardOutput, "stdout", attachedCallId, cancellationToken),
            PumpStreamAsync(session, session.Process.StandardError, "stderr", attachedCallId, cancellationToken));
    }

    private static async Task PumpStreamAsync(
        TerminalSession session,
        StreamReader reader,
        string stream,
        Func<long> attachedCallId,
        CancellationToken cancellationToken)
    {
        var buffer = new char[MaxEventChunkChars];
        var builder = new StringBuilder(MaxEventChunkChars);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;

                var chunk = new string(buffer, 0, read);
                AppendReplay(session, chunk);

                builder.Append(chunk);
                // Coalesce bursts: flush when the batch is large or the reader is idle.
                while (builder.Length >= MaxEventChunkChars)
                {
                    EmitChunk(session, attachedCallId, stream, builder.ToString(0, MaxEventChunkChars));
                    builder.Remove(0, MaxEventChunkChars);
                }
                if (reader.Peek() < 0 && builder.Length > 0)
                {
                    EmitChunk(session, attachedCallId, stream, builder.ToString());
                    builder.Clear();
                }
            }
            if (builder.Length > 0)
                EmitChunk(session, attachedCallId, stream, builder.ToString());
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
        catch (Exception)
        {
            // Stream died with the process; exit handling reports the outcome.
        }
    }

    private static void EmitChunk(TerminalSession session, Func<long> attachedCallId, string stream, string data)
    {
        if (data.Length == 0) return;
        EmitEvent(attachedCallId(), "terminal.stdout", writer =>
        {
            writer.WriteString("terminal_session_id", session.TerminalSessionId);
            writer.WriteString("stream", stream);
            writer.WriteString("data", data);
        });
    }

    /// <summary>Starts a long-lived shell session and returns it already registered.</summary>
    internal static TerminalSession StartSession(
        string shellFileName,
        string shellArguments,
        string workingDirectory,
        string command)
    {
        var startInfo = new ProcessStartInfo(shellFileName, shellArguments)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start shell '{shellFileName}'.");

        var session = new TerminalSession
        {
            TerminalSessionId = $"ts-{Guid.NewGuid():N}",
            Command = command,
            WorkingDirectory = workingDirectory,
            Process = process,
            StartedAt = DateTimeOffset.UtcNow
        };
        Register(session);

        var lifetimeCts = new CancellationTokenSource();
        _ = Task.Run(() => AwaitExitAsync(session, lifetimeCts.Token));
        StartOutputPumps(session, () => Volatile.Read(ref session.AttachedCallId), lifetimeCts.Token);
        return session;
    }

    private static async Task AwaitExitAsync(TerminalSession session, CancellationToken cancellationToken)
    {
        try
        {
            await session.Process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        session.Exited = true;
        session.ExitCode = session.Process.ExitCode;
        // Let the output pumps flush first so the exit marker never overtakes
        // the session's last chunk of output.
        try
        {
            await Task.WhenAny(session.OutputDrained, Task.Delay(1000)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort.
        }
        // Broadcast: no call is attached; Core correlates via terminal_session_id.
        EmitEvent(-1, "terminal.exit", writer =>
        {
            writer.WriteString("terminal_session_id", session.TerminalSessionId);
            writer.WriteNumber("exit_code", session.ExitCode);
            writer.WriteBoolean("timed_out", session.TimedOut);
        });
    }
}

// ── 会话式执行 ────────────────────────────────────────────────────────────────

/// <summary>One-shot result payload returned by the <c>shell</c> tool.</summary>
public sealed record ShellToolResult(
    bool Success,
    string TerminalSessionId,
    string Status,          // completed | long_lived | timed_out | failed | rejected
    int ExitCode,
    string Stdout,
    string Stderr,
    bool StdoutTruncated,
    bool StderrTruncated,
    bool TimedOut,
    long DurationMs,
    string? Error = null);

/// <summary>
/// Session-aware terminal execution built on top of <see cref="TerminalRunner"/>'s
/// process semantics: one-shot commands buffer and return as before, while
/// <c>long_lived</c> commands promote into a hosted <see cref="TerminalSession"/>
/// whose output keeps streaming as wire events.
/// </summary>
public static class TerminalSessionRunner
{
    private const int MaxCapturedChars = 64 * 1024;
    private const long LongLivedSettleMs = 3_000;

    public static async ValueTask<ShellToolResult> RunAsync(
        string shellFileName,
        string shellArguments,
        string workingDirectory,
        string command,
        int timeoutMs,
        bool longLived,
        long callId,
        CancellationToken cancellationToken)
    {
        if (!TerminalSessionHost.CanAdmit)
        {
            return new ShellToolResult(false, string.Empty, "rejected", -1, string.Empty, string.Empty,
                false, false, false, 0, "Too many active terminal sessions.");
        }

        var stopwatch = Stopwatch.StartNew();
        var session = TerminalSessionHost.StartSession(shellFileName, shellArguments, workingDirectory, command);
        var process = session.Process;
        session.AttachedCallId = callId;

        if (longLived)
        {
            // Give fast-failing commands a moment to surface their error, then
            // hand control back: the call completes, the session lives on.
            var settle = Task.Delay(TimeSpan.FromMilliseconds(LongLivedSettleMs), cancellationToken);
            var exitTask = process.WaitForExitAsync(cancellationToken);
            var finished = await Task.WhenAny(
                exitTask,
                Task.Delay(TimeSpan.FromMilliseconds(LongLivedSettleMs), cancellationToken)).ConfigureAwait(false);

            if (finished == exitTask)
            {
                // Exited almost immediately: treat as a regular completed call.
                await DrainOutputAsync(session, 500).ConfigureAwait(false);
                session.AttachedCallId = -1;
                session.Exited = true;
                var output = TerminalSessionHost.ReadReplay(session);
                var (stdout, stderr, truncated) = SplitReplay(output);
                return new ShellToolResult(process.ExitCode == 0, session.TerminalSessionId, "completed",
                    process.ExitCode, stdout, stderr, truncated, truncated, false, stopwatch.ElapsedMilliseconds);
            }

            // Flush whatever arrived during the settle window before the response,
            // then let the session keep streaming as a broadcast session.
            await DrainOutputAsync(session, 500).ConfigureAwait(false);
            session.AttachedCallId = -1;
            return new ShellToolResult(true, session.TerminalSessionId, "long_lived", -1,
                TerminalSessionHost.ReadReplay(session), string.Empty, false, false, false,
                stopwatch.ElapsedMilliseconds);
        }

        // One-shot: bounded wait, then kill the whole tree.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeoutMs > 0) timeoutCts.CancelAfter(timeoutMs);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
        }
        catch (OperationCanceledException)
        {
            session.Exited = true;
            throw;
        }

        if (timedOut)
        {
            session.TimedOut = true;
            session.Exited = true;
            TerminalSessionHost.KillSafe(process);
            await DrainOutputAsync(session, 1000).ConfigureAwait(false);
            var output = TerminalSessionHost.ReadReplay(session);
            var (stdout, stderr, truncated) = SplitReplay(output);
            return new ShellToolResult(false, session.TerminalSessionId, "timed_out", -1,
                stdout, stderr, truncated, truncated, true, stopwatch.ElapsedMilliseconds,
                $"Command timed out after {timeoutMs}ms and was terminated.");
        }

        // Emit buffered output events *before* the response line, otherwise the
        // caller detaches its observer as soon as the response lands and the
        // tail of the output is lost.
        await DrainOutputAsync(session, 1000).ConfigureAwait(false);
        session.AttachedCallId = -1;
        session.Exited = true;
        var finalOutput = TerminalSessionHost.ReadReplay(session);
        var (finalStdout, finalStderr, finalTruncated) = SplitReplay(finalOutput);
        return new ShellToolResult(process.ExitCode == 0, session.TerminalSessionId, "completed",
            process.ExitCode, finalStdout, finalStderr, finalTruncated, finalTruncated, false,
            stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Waits briefly for the output pumps to flush. Bounded so a wedged stream can
    /// never stall the tool response.
    /// </summary>
    private static async Task DrainOutputAsync(TerminalSession session, int maxWaitMs)
    {
        try
        {
            await Task.WhenAny(session.OutputDrained, Task.Delay(maxWaitMs)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort flush.
        }
    }

    private static (string Stdout, string Stderr, bool Truncated) SplitReplay(string combined)
    {
        // Replay interleaves both streams; the buffered result is informational —
        // the authoritative live stream is the event channel.
        var truncated = combined.Length > MaxCapturedChars;
        if (truncated) combined = combined[..MaxCapturedChars];
        return (combined, string.Empty, truncated);
    }
}
